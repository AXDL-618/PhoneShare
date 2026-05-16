using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace PhoneShareReceiver;

public partial class App : System.Windows.Application
{
    private const string MutexName = "PhoneShareReceiver.SingleInstance.v1";
    private const string PipeName = "PhoneShareReceiver.SingleInstance.Pipe.v1";

    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private CancellationTokenSource? _pipeCts;
    private MainWindow? _mainWindow;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(true, MutexName, out _ownsMutex);

        // 已经有一个实例在运行：通知旧实例显示主窗口，然后当前实例退出
        if (!_ownsMutex)
        {
            NotifyExistingInstance();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Show();

        StartPipeServer();
    }

    private static void NotifyExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out
            );

            client.Connect(600);

            using var writer = new StreamWriter(client)
            {
                AutoFlush = true
            };

            writer.WriteLine("SHOW");
        }
        catch
        {
            // 通知失败也直接退出，避免启动第二个实例导致端口冲突
        }
    }

    private void StartPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous
                    );

                    await server.WaitForConnectionAsync(token);

                    using var reader = new StreamReader(server);
                    var command = await reader.ReadLineAsync();

                    if (string.Equals(command, "SHOW", StringComparison.OrdinalIgnoreCase))
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _mainWindow?.ShowMainWindowFromTray();
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try
                    {
                        await Task.Delay(200, token);
                    }
                    catch
                    {
                        break;
                    }
                }
            }
        }, token);
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try
        {
            _pipeCts?.Cancel();
            _pipeCts?.Dispose();
        }
        catch
        {
        }

        try
        {
            if (_ownsMutex)
            {
                _singleInstanceMutex?.ReleaseMutex();
            }

            _singleInstanceMutex?.Dispose();
        }
        catch
        {
        }

        base.OnExit(e);
    }
}