# 使用 .NET 8.0 运行时镜像来运行应用，并安装 Python
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
# 设置镜像加速器
RUN echo "Types: deb" > /etc/apt/sources.list.d/debian.sources
RUN echo "URIs: https://mirrors.tuna.tsinghua.edu.cn/debian" >> /etc/apt/sources.list.d/debian.sources
RUN echo "Suites: bookworm bookworm-updates bookworm-backports" >> /etc/apt/sources.list.d/debian.sources
RUN echo "Components: main contrib non-free non-free-firmware" >> /etc/apt/sources.list.d/debian.sources
RUN echo "Signed-By: /usr/share/keyrings/debian-archive-keyring.gpg" >> /etc/apt/sources.list.d/debian.sources

# 安装系统依赖（Python 和 venv）
RUN apt-get update && \
    apt-get install -y python3 python3-venv python3-pip && \
    apt-get install -y xorg \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/* \
    xhost +local:yuchen 

# 如果你有其他 Python 库，可以通过 requirements.txt 文件进行安装
COPY requirements.txt ./

# 创建虚拟环境并安装依赖
RUN python3 -m venv venv && \
    ./venv/bin/pip config set global.index-url https://pypi.tuna.tsinghua.edu.cn/simple && \
    ./venv/bin/pip install --upgrade pip && \
    ./venv/bin/pip install -r requirements.txt
   
COPY ./bin/Release/net8.0/linux-x64/publish .

ENTRYPOINT ["./v2rayN.Desktop"]
