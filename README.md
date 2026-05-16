# PhoneShare

PhoneShare 是一个轻量级的 **Android → Windows 局域网文件传输工具**。

通过二维码完成一次配对后，用户可以直接从安卓系统“分享”菜单中，将照片、视频、文档等文件发送到 Windows 电脑的指定文件夹。PhoneShare 不依赖云服务器，文件在手机与电脑之间通过局域网直连传输。

## 功能特性

- Android 向 Windows 局域网传输文件
- 二维码扫码配对
- 支持 Android 系统分享菜单
- 支持单文件与多文件发送
- Windows 端托盘常驻
- 可自定义接收文件夹
- 支持已配对手机管理与改名
- 基于 Token 的配对与上传鉴权
- 无需云服务器



## 下载

请前往 [Releases](../../releases) 下载最新版本。

| 平台    | 文件                                    |
| ------- | --------------------------------------- |
| Windows | `PhoneShareReceiver-v1.0.0-win-x64.exe` |
| Android | `PhoneShare-v1.0.0.apk`                 |

Windows 端发布版为自包含单文件 EXE，普通用户无需额外安装 .NET 运行环境。



## 使用方法

### 1. 启动 Windows 接收端

运行 Windows 端程序：

```text
PhoneShareReceiver-v1.0.0-win-x64.exe
```

首次使用时，选择文件接收目录。若 Windows 防火墙弹窗，建议允许“专用网络”访问。

启动后，窗口中会显示用于配对的二维码。

### 2. 安装 Android 发送端

在安卓手机上安装：

```text
PhoneShare-v1.0.0.apk
```

打开 App，点击“扫码绑定电脑”，扫描 Windows 端二维码完成配对。

### 3. 发送文件

在手机相册、文件管理器或其他支持分享的应用中：

```text
选择文件 → 分享 → PhoneShare → 选择目标电脑
```

文件会自动保存到 Windows 端设置的接收目录。

或者直接从PhoneShare软件中选择本地文件分享也可。



## 系统要求

### Windows

- Windows 10 / Windows 11
- x64 架构
- 与 Android 设备处于同一局域网

### Android

- Android 7.0 及以上
- 相机权限用于扫码配对
- 通知权限用于显示传输状态



## 安全说明

PhoneShare 用于可信局域网内的设备间文件传输。文件不会经过云服务器。

请勿将 Windows 接收端口暴露到公网，也不建议在不可信网络环境下使用。



## 开源协议

本项目采用 MIT License。
