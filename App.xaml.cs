using System.Windows;

namespace CodexMeter;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\CodexMeter.SingleInstance.v1";
    private const string ActivationEventName = @"Local\CodexMeter.Activate.v1";

    private MainWindow? _window;
    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationSignal;
    private RegisteredWaitHandle? _activationRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        _activationSignal = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: ActivationEventName);
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationSignal,
            (_, _) =>
            {
                if (!Dispatcher.HasShutdownStarted)
                    _ = Dispatcher.BeginInvoke(ActivateMainWindow);
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);

        _window = new MainWindow();
        _window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationRegistration?.Unregister(null);
        _activationSignal?.Dispose();
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private void ActivateMainWindow() => _window?.ShowAndActivate();

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var activationSignal = EventWaitHandle.OpenExisting(ActivationEventName);
                activationSignal.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
