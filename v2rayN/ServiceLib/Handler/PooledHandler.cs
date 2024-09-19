using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using ReactiveUI;
using ServiceLib.ViewModels;
using Splat;

namespace ServiceLib.Handler
{
    public class PooledHandler
    {
        private static readonly Lazy<PooledHandler> _instance = new(() => new());
        public static PooledHandler Instance => _instance.Value;

        private Action<bool, string> _updateFunc;
        private Config _config;
        private static Semaphore _semaphore = new Semaphore(1, 1);

        public PooledHandler()
        {
        }

        public void RegUpdateTask(Config config, Action<bool, string> update)
        {
            _config = config;
            _updateFunc = update;
            Task.Run(UpdateTaskRunCrawler);
            Task.Run(UpdateTaskRunScore);
            Task.Run(UpdateTaskRunPooling);
        }

        private async Task UpdateTaskRunCrawler()
        {
            await Task.Delay(1000 * 5);
            while (true)
            {
                UpdateCrawlerAll();
                await Task.Delay(1000 * 86400); // 每天采集一次 
            }
        }

        private async Task UpdateTaskRunScore()
        {
            await Task.Delay(1000 * 10);
            while (true)
            {
                UpdateScoreAll();
                await Task.Delay(1000 * 3600); // 每小时计算一次 
            }
        }

        private async Task UpdateTaskRunPooling()
        {
            await Task.Delay(1000 * 15);
            while (true)
            {
                UpdatePoolingNow();
                await Task.Delay(1000 * 60); // 每分钟筛选一次 
            }
        }

        public void UpdateCrawlerAll()
        {
            Task.Run(async () =>
            {
                _semaphore.WaitOne();
                try
                {
                    _updateFunc(false, "Start UpdateCrawler All");
                    var directoryPath = Utils.GetScriptPath();
                    // 获取所有 .py 文件
                    string[] pythonFiles = Directory.GetFiles(directoryPath, "*.py");

                    // 文件列表
                    foreach (string file in pythonFiles)
                    {
                        try
                        {
                            await UpdateCrawler(file);
                        }
                        catch (Exception ex)
                        {
                            _updateFunc(false, ex.Message);
                        }
                    }

                    MessageBus.Current.SendMessage("", Global.CommandRefreshSubscriptions);
                    _updateFunc(true, string.Format("End UpdateCrawler({0})", pythonFiles.Length));
                }
                catch (Exception ex)
                {
                    Logging.SaveLog(ex.Message, ex);
                }
                finally
                {
                    _semaphore.Release();
                }
            });
        }

        private async Task UpdateCrawler(string filePath)
        {
            string fileName = Path.GetFileName(filePath);
            // 创建一个新的进程
            ProcessStartInfo psi = new ProcessStartInfo();

            // 设置进程启动信息
            psi.FileName = Utils.IsWindows() ? "cmd.exe" : "/bin/bash";
            // 可选：设置是否使用操作系统的窗口
            psi.UseShellExecute = false; // 如果希望在后台运行
            psi.RedirectStandardInput = true; // 重定向输入流
            psi.RedirectStandardOutput = true; // 如果需要重定向输出
            psi.RedirectStandardError = true; // 如果需要重定向错误
            psi.CreateNoWindow = true; // 不创建窗口
            using (Process process = Process.Start(psi))
            {
                // 向命令行输入启动命令
                process.StandardInput.WriteLine($"python \"{filePath}\"");
                process.StandardInput.WriteLine("exit"); // 退出命令行
                // 可选：读取输出
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _updateFunc(false, error);
                }

                if (string.IsNullOrWhiteSpace(output))
                {
                    _updateFunc(false, string.Format("The content obtained by the script is empty：{0}", fileName));
                }

                // 按行分割字符串
                string[] lines = output.Split(new[] { '\n' }, StringSplitOptions.None);
                // 遍历每一行
                bool start = false;
                int count = 0;
                int totalLine = 0;
                foreach (string line in lines)
                {
                    if (line.StartsWith("**************************************"))
                    {
                        if (start) break;
                        start = true;
                        continue;
                    }

                    if (start)
                    {
                        string strData = line;
                        if (Utils.IsBase64String(line))
                        {
                            strData = Utils.Base64Decode(line);
                            // 按行分割字符串
                            string[] new_lines = strData.Split(new[] { '\n' }, StringSplitOptions.None);
                            foreach (string newLine in new_lines)
                            {
                                totalLine++;
                                strData = newLine;
                                int ret = ConfigHandler.AddBatchServers(_config, strData, null, true);
                                count += ret;
                            }
                        }
                        else
                        {
                            totalLine++;
                            int ret = ConfigHandler.AddBatchServers(_config, strData, null, true);
                            count += ret;
                        }
                    }
                }

                _updateFunc(false, $"CrawlerName:{fileName}, Total:{totalLine}, Success:{count}");
            }
        }

        public void DispatchSubid(ProfileItem profileItem)
        {
            string url = $"http://demo.ip-api.com/json/{profileItem.address}?fields=66842623&lang=en";
            var downloadHandle = new DownloadHandler();
            string result = downloadHandle.TryDownloadString(url, false, Global.UserAgentTexts[Global.UserAgent[0]])
                .Result ?? "Other";
            var deserialize = JsonUtils.Deserialize<Dictionary<string, object>>(result) ??
                              new Dictionary<string, object>();
            string? status = deserialize["status"].ToString();
            result = status == "success" ? result = deserialize["country"].ToString() ?? "Other" : "Other";

            var subItems = LazyConfig.Instance.SubItems();
            var subItem = subItems.FirstOrDefault(s => s.remarks == result);
            if (subItem == null)
            {
                subItem = new()
                {
                    id = string.Empty,
                    remarks = result,
                    enabled = false
                };
                ConfigHandler.AddSubItem(_config, subItem);
            }

            profileItem.subid = subItem.id;
            _updateFunc(false, $"Remarks:[{profileItem.remarks}],Address:[{profileItem.address}], Subscription[{subItem.remarks}]");
        }

        public void UpdateScoreAll()
        {
            Task.Run(async () =>
            {
                _semaphore.WaitOne();
                _updateFunc(false, "Start counting all scores.");
                CoreHandler? coreHandler = null;
                int pid = -1;
                try
                {
                    coreHandler = Locator.Current.GetService<CoreHandler>();
                    if (coreHandler == null) return;
                    var items = LazyConfig.Instance.ProfileItems(null);
                    items = items.Where(item => item.port > 0 && item.configType != EConfigType.Custom)
                        .ToList<ProfileItem>();
                    List<ServerTestItem> _selecteds = new List<ServerTestItem>();
                    foreach (var it in items)
                    {
                        if (it.configType == EConfigType.Custom)
                        {
                            continue;
                        }

                        if (it.port <= 0)
                        {
                            continue;
                        }

                        _selecteds.Add(new ServerTestItem()
                        {
                            indexId = it.indexId,
                            address = it.address,
                            port = it.port,
                            configType = it.configType
                        });
                    }

                    pid = coreHandler.LoadCoreConfigSpeedtest(_selecteds);
                    if (pid < 0)
                    {
                        _updateFunc(false, ResUI.FailedToRunCore);
                        return;
                    }

                    DownloadHandler downloadHandle = new DownloadHandler();

                    List<Task> tasks = new();
                    foreach (var it in _selecteds)
                    {
                        ScoreTestResult res = new()
                            { IndexId = it.indexId, Delay = ResUI.Speedtesting, Score = ResUI.Speedtesting };
                        MessageBus.Current.SendMessage(res, Global.CommandScoreTestResult);

                        ProfileExHandler.Instance.SetTestDelay(it.indexId, "0");

                        if (!it.allowTest)
                        {
                            continue;
                        }

                        if (it.configType == EConfigType.Custom)
                        {
                            continue;
                        }

                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                WebProxy webProxy = new(Global.Loopback, it.port);
                                int responseTime =
                                    await downloadHandle.GetRealPingTime(_config.speedTestItem.speedPingTestUrl,
                                        webProxy, 10);

                                // 计算分数
                                int score = ProfileExHandler.Instance.GetScore(it.indexId);
                                score = responseTime > 0 ? score + 1 : score - 1;

                                ProfileExHandler.Instance.SetTestDelay(it.indexId, responseTime.ToString());
                                ProfileExHandler.Instance.SetTestScore(it.indexId, score);


                                ScoreTestResult res = new()
                                    { IndexId = it.indexId, Delay = responseTime.ToString(), Score = score.ToString() };
                                MessageBus.Current.SendMessage(res, Global.CommandScoreTestResult);

                                it.delay = responseTime;
                            }
                            catch (Exception ex)
                            {
                                Logging.SaveLog(ex.Message, ex);
                            }
                        }));
                    }

                    Task.WaitAll(tasks.ToArray());
                    // 自动删除分数<0的节点
                    List<ProfileExItem> waitDelExs = ProfileExHandler.Instance.ProfileExs.Where(p => p.score < 0).ToList();
                    if (waitDelExs.Count > 0)
                    {
                        List<ProfileItem> waitDels = new List<ProfileItem>();
                        foreach (var it in waitDelExs)
                        {
                            ProfileItem? profileItem = LazyConfig.Instance.GetProfileItem(it.indexId);
                            if (profileItem != null)
                            {
                                waitDels.Add(profileItem);
                            }
                        }

                        if (waitDels.Count > 0)
                        {
                            ConfigHandler.RemoveServer(_config, waitDels);
                        }
                    }
                    
                }
                catch (Exception ex)
                {
                    Logging.SaveLog(ex.Message, ex);
                }
                finally
                {
                    if (pid > 0)
                    {
                        coreHandler.CoreStopPid(pid);
                    }
                    
                    if (ConfigHandler.SortServers(_config, null, "scoreVal", false) == 0)
                    {
                        MessageBus.Current.SendMessage("", Global.CommandRefreshProfiles);
                    }

                    ProfileExHandler.Instance.SaveTo();
                    _updateFunc(false, "End counting all scores");
                    _semaphore.Release();
                }
            });
        }

        public void UpdatePoolingNow()
        {
            Task.Run(async () =>
            {
                _semaphore.WaitOne();
                _updateFunc(false, "Start filtering pooled nodes.");
                int pid = -1;
                CoreHandler? coreHandler = null;
                try
                {
                    ConcurrentBag<ProfileExItem> profileExs = ProfileExHandler.Instance.ProfileExs;
                    if(profileExs == null || profileExs.Count == 0) return;
                    List<ProfileExItem> profileExItems = profileExs.Where(p=> p.delay > -1)
                        .OrderByDescending(p => p.score)
                        .ThenBy(p =>p.delay)
                        .Take(10)
                        .ToList();
                    List<ServerTestItem> _selecteds = new List<ServerTestItem>();
                    foreach (var profileExItem in profileExItems)
                    {
                        var it = LazyConfig.Instance.GetProfileItem(profileExItem.indexId);

                        if (it.configType == EConfigType.Custom)
                        {
                            continue;
                        }

                        if (it.port <= 0)
                        {
                            continue;
                        }

                        _selecteds.Add(new ServerTestItem()
                        {
                            indexId = it.indexId,
                            address = it.address,
                            port = it.port,
                            configType = it.configType
                        });
                    }
                    // 前十个节点测速，取最快的
                    coreHandler = Locator.Current.GetService<CoreHandler>();
                    if (coreHandler == null) return;
                    pid = coreHandler.LoadCoreConfigSpeedtest(_selecteds);
                    if (pid < 0)
                    {
                        _updateFunc(false, ResUI.FailedToRunCore);
                        return;
                    }
                    string url = _config.speedTestItem.speedTestUrl;
                    var timeout = _config.speedTestItem.speedTestTimeout;

                    DownloadHandler downloadHandle = new();

                    foreach (var it in _selecteds)
                    {
                        if (!it.allowTest)
                        {
                            continue;
                        }
                        if (it.configType == EConfigType.Custom)
                        {
                            continue;
                        }
                        ProfileExHandler.Instance.SetTestSpeed(it.indexId, "-1");
                        
                        SpeedTestResult res = new()
                            { IndexId = it.indexId, Speed = ResUI.Speedtesting };
                        MessageBus.Current.SendMessage(res, Global.CommandSpeedTestResult);

                        var item = LazyConfig.Instance.GetProfileItem(it.indexId);
                        if (item is null) continue;

                        WebProxy webProxy = new(Global.Loopback, it.port);

                        await downloadHandle.DownloadDataAsync(url, webProxy, timeout, (bool success, string msg) =>
                        {
                            decimal.TryParse(msg, out decimal dec);
                            if (dec > 0)
                            {
                                ProfileExHandler.Instance.SetTestSpeed(it.indexId, msg);
                            }
                            SpeedTestResult res = new()
                                { IndexId = it.indexId, Speed = msg };
                            MessageBus.Current.SendMessage(res, Global.CommandSpeedTestResult);
                        });
                    }

                    if (pid > 0)
                    {
                        coreHandler.CoreStopPid(pid);
                    }
                    ProfileExHandler.Instance.SaveTo();
                    
                    // 计算并切换最快节点
                    ProfileExItem? maxItem = profileExItems.MaxBy(p => p.speed);
                    if (maxItem == null || maxItem.indexId == _config.indexId)
                    {
                        return;
                    }
                    
                    if (ConfigHandler.SetDefaultServerIndex(_config, maxItem.indexId) == 0)
                    {
                        /*MessageBus.Current.SendMessage("", Global.CommandRefreshProfiles);
                        Locator.Current.GetService<MainWindowViewModel>()?.Reload();*/
                    }
                }
                catch (Exception ex)
                {
                    Logging.SaveLog(ex.Message, ex);
                }
                finally
                {
                    _updateFunc(true, "End filtering pooled nodes.");
                    _semaphore.Release();
                }
            });
        }

        [Serializable]
        public class ScoreTestResult
        {
            public string? IndexId { get; set; }

            public string? Delay { get; set; }

            public string? Score { get; set; }
        }
    }
}