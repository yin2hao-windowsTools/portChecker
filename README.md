# portChecker

`portChecker` 是一个使用C# 语言开发的，在Windows平台上查看当前Windows端口详情，并提供快捷释放端口功能的软件。

## 当前能力
1. 查看当前监听/连接中的端口、Windows svchost.exe 关联的服务端口以及 Windows 保留端口信息
2. 快速搜索指定端口的占用情况
3. 提供辅助释放端口的快捷操作，例如结束相关进程或提示对应服务信息


## 注意事项

> 释放端口可能会结束相关进程或影响系统服务，请谨慎操作。

## 运行说明
本项目基于 .NET 8.0 构建。

为减少安装包体积，安装包内部未内置 .NET 8.0 Runtime。因此，用户电脑需要提前安装 .NET 8.0 Runtime 才能正常运行。

点击[这里](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)前往下载 Microsoft 提供的 .NET 8.0 runtime
