using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using ReactiveUI;
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
        
        public void UpdateCrawlerAll()
        {
            Task.Run(async () =>
            {
                _semaphore.WaitOne();
                _updateFunc(false, "更新全部爬虫开始");
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
                _updateFunc(true, string.Format("更新全部爬虫({0})结束", pythonFiles.Length));
                _semaphore.Release();
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
                    _updateFunc(false, string.Format("脚本获取的内容为空：{0}", fileName));
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
                    if(start)
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
                _updateFunc(false, $"爬虫名称{fileName}, 共爬取数量{totalLine}, 解析成功{count}");
            }
        }

        public void DispatchSubid(ProfileItem profileItem)
        {
            string url = $"http://demo.ip-api.com/json/{profileItem.address}?fields=66842623&lang=en";
            var downloadHandle = new DownloadHandler();
            string result = downloadHandle.TryDownloadString(url, false, Global.UserAgentTexts[Global.UserAgent[0]]).Result ?? "Other";
            var deserialize = JsonUtils.Deserialize<Dictionary<string, object>>(result) ?? new Dictionary<string, object>();
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
            _updateFunc(false, $"别名[{profileItem.remarks}],地址[{profileItem.address}], 订阅分组[{subItem.remarks}]");
        }

        public void UpdateScoreAll()
        {
            Task.Run(async () =>
            {
                _semaphore.WaitOne();
                _updateFunc(false, "计算所有分数开始");
                var coreHandler = Locator.Current.GetService<CoreHandler>();
                if (coreHandler == null) return;
                var items = LazyConfig.Instance.ProfileItems(null);
                items = items.Where(item => item.port > 0 && item.configType != EConfigType.Custom).ToList<ProfileItem>();
                
                int pid = -1;
                try
                {
                    string msg = string.Empty;

                    pid = coreHandler.LoadCoreConfigSpeedtest(_selecteds);
                    if (pid < 0)
                    {
                        UpdateFunc("", ResUI.FailedToRunCore)

                        return Task.CompletedTask;
                    }

                    DownloadHandler downloadHandle = new DownloadHandler();

                    List<Task> tasks = new();
                    foreach (var it in items)
                    {
                        ScoreTestResult res = new() { IndexId = it.indexId, Delay = ResUI.Speedtesting, Score = ResUI.Speedtesting };
                        MessageBus.Current.SendMessage(res, Global.CommandScoreTestResult);

                        ProfileExHandler.Instance.SetTestDelay(it.indexId, "0");

                        if (it.configType == EConfigType.Custom)
                        {
                            continue;
                        }
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                WebProxy webProxy = new(Global.Loopback, it.port);
                                
                                int responseTime = await downloadHandle.GetRealPingTime(_config.speedTestItem.speedPingTestUrl, webProxy, 10);
                                ProfileExHandler.Instance.SetTestDelay(it.indexId, $"{responseTime}");
                                ProfileExHandler.Instance.SetTestScore(it.indexId, 0);
                                UpdateFunc(it.indexId, output);
                                int.TryParse(output, out int delay);
                                it.delay = delay;
                            }
                            catch (Exception ex)
                            {
                                Logging.SaveLog(ex.Message, ex);
                            }
                        }));
                    }
                    Task.WaitAll(tasks.ToArray());
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
                    ProfileExHandler.Instance.SaveTo();
                }

                _updateFunc(false, "计算所有分数完成");
                _semaphore.Release();
            });
            
                
        }
    }

    [Serializable]
    public class ScoreTestResult
    {
        public string? IndexId { get; set; }

        public string? Delay { get; set; }

        public string? Score { get; set; }
    }
}