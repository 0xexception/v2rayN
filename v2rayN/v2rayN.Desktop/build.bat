@echo off
REM --sc: --self-contained 生成一个自包含运行时的应用程序，无需在目标机器上安装 .NET 运行时，可以直接运行应用程序
REM -r 指定目标运行环境（Runtime Identifier, RID）
dotnet publish --sc -r linux-x64

docker build -t v2ray-n .
docker save v2ray-n -o v2ray-n.tar
pause