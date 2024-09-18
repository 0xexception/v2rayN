@echo off
dotnet publish --sc -r linux-x64
docker build -t v2ray-n .
pause