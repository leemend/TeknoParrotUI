using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using TeknoParrotUi.Avalonia.Services;

namespace TeknoParrotUi.Avalonia.Views;

public partial class RemotePlayManagementView : UserControl
{
    private readonly DispatcherTimer _timer;
    private bool _sunshineBusy;
    private bool _refreshing;
    private bool _updatingMode;
    private bool _moonlightBusy;
    private bool _moonlightChecking;
    private bool _moonlightReady;
    private DateTime _lastMoonlightCheck = DateTime.MinValue;

    public event Action? CloseRequested;

    public RemotePlayManagementView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        Loaded += LoadedView;
        Unloaded += (_, _) => _timer.Stop();
    }

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private async void LoadedView(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        RefreshMoonlightInstallState();
        if (!OperatingSystem.IsWindows())
        {
            SunshineStatusText.Text = "Windows only";
            SunshineStatusDetail.Text = "Sunshine/Moonlight portable management is currently implemented for Windows.";
            SetSunshineControls(false);
            return;
        }

        await RefreshSunshineAsync();
        _timer.Start();
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        if (!_sunshineBusy && !_refreshing) await RefreshSunshineAsync();

        if (!_moonlightBusy && !_moonlightChecking &&
            DateTime.UtcNow - _lastMoonlightCheck >= TimeSpan.FromSeconds(4))
        {
            _lastMoonlightCheck = DateTime.UtcNow;
            await RefreshMoonlightHostStatusAsync();
        }
    }

    private async Task RefreshSunshineAsync(bool clients = true)
    {
        if (_refreshing || !OperatingSystem.IsWindows()) return;
        _refreshing = true;
        try
        {
            if (!SunshineManager.IsInstalled()) { ShowSunshineMissing(); return; }
            if (!SunshineManager.IsRunning()) { ShowSunshineStopped(); return; }

            try
            {
                var status = await SunshineManager.GetStatusAsync();
                ShowSunshineRunning(status);
                if (clients) await RefreshClientsAsync();
            }
            catch (Exception ex)
            {
                SunshineStatusText.Text = "Running";
                SunshineStatusDetail.Text = "Sunshine is running, but the managed API is not available yet.";
                ManagedApiDetailText.Text = "API: " + ex.Message;
                BtnStartSunshine.IsEnabled = false;
                BtnStopSunshine.IsEnabled = !_sunshineBusy;
                BtnRestartSunshine.IsEnabled = !_sunshineBusy;
                BtnOpenSunshineWebUi.IsEnabled = !_sunshineBusy;
                SetManagedControls(false);
            }
        }
        finally { _refreshing = false; }
    }

    private void ShowSunshineMissing()
    {
        SunshineStatusText.Text = "Sunshine not found";
        SunshineStatusDetail.Text = $"Expected: {SunshineManager.SunshineExecutablePath}";
        ManagedApiDetailText.Text = "Managed API disconnected";
        SetSunshineControls(false);
        ClearManaged();
    }

    private void ShowSunshineStopped()
    {
        SunshineStatusText.Text = "Stopped";
        SunshineStatusDetail.Text = "Sunshine is installed and ready to start.";
        ManagedApiDetailText.Text = "Managed API disconnected";
        BtnStartSunshine.IsEnabled = !_sunshineBusy;
        BtnStopSunshine.IsEnabled = false;
        BtnRestartSunshine.IsEnabled = false;
        BtnOpenSunshineWebUi.IsEnabled = false;
        SetManagedControls(false);
        ClearManaged();
    }

    private void ShowSunshineRunning(SunshineStatus status)
    {
        SunshineStatusText.Text = "Running";
        SunshineStatusDetail.Text = $"Sunshine {status.Version} is running in TeknoParrot managed mode.";
        ManagedApiDetailText.Text = string.IsNullOrWhiteSpace(status.Platform)
            ? "Managed API connected"
            : $"Managed API connected • {status.Platform}";

        BtnStartSunshine.IsEnabled = false;
        BtnStopSunshine.IsEnabled = !_sunshineBusy;
        BtnRestartSunshine.IsEnabled = !_sunshineBusy;
        BtnOpenSunshineWebUi.IsEnabled = !_sunshineBusy;
        SetManagedControls(!_sunshineBusy);

        _updatingMode = true;
        try
        {
            RadioConnectionOpen.IsChecked = status.ConnectionMode.Equals("open", StringComparison.OrdinalIgnoreCase);
            RadioConnectionClosed.IsChecked = !RadioConnectionOpen.IsChecked;
        }
        finally { _updatingMode = false; }

        ConnectionStateText.Text = status.ConnectionOpen ? "Open" : "Closed";
        ActiveSessionsText.Text = status.ActiveSessions.ToString();
        PairedClientsText.Text = status.PairedClients.ToString();
        ConnectionModeDetailText.Text = "Managed by TeknoParrot while Sunshine is running.";
        if (!_sunshineBusy)
            PairingStatusText.Text = status.PairingPending ? "Pairing request is currently waiting" : "Waiting on pairing requests";
    }

    private void ClearManaged()
    {
        _updatingMode = true;
        try { RadioConnectionOpen.IsChecked = RadioConnectionClosed.IsChecked = false; }
        finally { _updatingMode = false; }

        ConnectionStateText.Text = ActiveSessionsText.Text = PairedClientsText.Text = "—";
        ConnectionModeDetailText.Text = "Sunshine is not running.";
        ClientsListBox.ItemsSource = null;
        ClientListStatusText.Text = "Sunshine is not running.";
    }

    private void SetSunshineControls(bool enabled)
    {
        BtnStartSunshine.IsEnabled = enabled;
        BtnStopSunshine.IsEnabled = enabled;
        BtnRestartSunshine.IsEnabled = enabled;
        BtnOpenSunshineWebUi.IsEnabled = enabled;
        SetManagedControls(enabled);
    }

    private void SetManagedControls(bool enabled)
    {
        RadioConnectionOpen.IsEnabled = RadioConnectionClosed.IsEnabled = enabled;
        PairingPinTextBox.IsEnabled = PairingNameTextBox.IsEnabled = BtnPairClient.IsEnabled = enabled;
        ClientsListBox.IsEnabled = BtnRefreshClients.IsEnabled = BtnDisconnectAll.IsEnabled = enabled;
        BtnUnpairClient.IsEnabled = enabled && ClientsListBox.SelectedItem != null;
    }

    private async Task RefreshClientsAsync()
    {
        try
        {
            var clients = await SunshineManager.GetClientsAsync();
            var selected = (ClientsListBox.SelectedItem as SunshineClientInfo)?.Uuid;
            ClientsListBox.ItemsSource = clients;
            if (!string.IsNullOrWhiteSpace(selected))
                ClientsListBox.SelectedItem = clients.FirstOrDefault(c => c.Uuid.Equals(selected, StringComparison.OrdinalIgnoreCase));

            ClientListStatusText.Text = clients.Count == 0
                ? "No paired Moonlight clients."
                : $"{clients.Count} paired client(s) • {clients.Count(c => c.Connected)} connected";
            BtnUnpairClient.IsEnabled = !_sunshineBusy && ClientsListBox.SelectedItem != null;
        }
        catch (Exception ex) { ClientListStatusText.Text = "Unable to load clients: " + ex.Message; }
    }

    private async void BtnStartSunshine_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sunshineBusy) return;
        try
        {
            _sunshineBusy = true;
            SetSunshineControls(false);
            SunshineStatusText.Text = "Starting...";
            SunshineStatusDetail.Text = "Launching Sunshine in TeknoParrot mode.";
            SunshineManager.Start();
            await SunshineManager.WaitForRunningStateAsync(true, TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) { await Error(ex, "Sunshine"); }
        finally { _sunshineBusy = false; await RefreshSunshineAsync(); }
    }

    private async void BtnStopSunshine_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sunshineBusy) return;
        try
        {
            _sunshineBusy = true;
            SetSunshineControls(false);
            SunshineStatusText.Text = "Stopping...";
            await SunshineManager.StopAsync();
        }
        catch (Exception ex) { await Error(ex, "Sunshine"); }
        finally { _sunshineBusy = false; await RefreshSunshineAsync(); }
    }

    private async void BtnRestartSunshine_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sunshineBusy) return;
        try
        {
            _sunshineBusy = true;
            SetSunshineControls(false);
            SunshineStatusText.Text = "Restarting...";
            await SunshineManager.RestartAsync();
        }
        catch (Exception ex) { await Error(ex, "Sunshine"); }
        finally { _sunshineBusy = false; await RefreshSunshineAsync(); }
    }

    private async void ConnectionMode_Checked(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_updatingMode || _sunshineBusy || !IsLoaded) return;
        try
        {
            _sunshineBusy = true;
            SetManagedControls(false);
            await SunshineManager.SetConnectionModeAsync(RadioConnectionOpen.IsChecked == true ? "open" : "closed");
        }
        catch (Exception ex) { await Error(ex, "Sunshine"); }
        finally { _sunshineBusy = false; await RefreshSunshineAsync(false); }
    }

    private async void BtnPairClient_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sunshineBusy) return;
        try
        {
            _sunshineBusy = true;
            SetManagedControls(false);
            PairingStatusText.Text = "Pairing...";
            await SunshineManager.PairAsync((PairingPinTextBox.Text ?? "").Trim(), (PairingNameTextBox.Text ?? "").Trim());
            PairingPinTextBox.Clear();
            PairingNameTextBox.Clear();
            PairingStatusText.Text = "Pairing accepted by Sunshine.";
            await RefreshClientsAsync();
        }
        catch (Exception ex)
        {
            PairingStatusText.Text = ex.Message.Equals("Sunshine rejected the pairing request.", StringComparison.OrdinalIgnoreCase)
                ? "This Moonlight client is already paired."
                : "Pairing failed.";
            if (!ex.Message.Equals("Sunshine rejected the pairing request.", StringComparison.OrdinalIgnoreCase))
                await Error(ex, "Sunshine");
        }
        finally { _sunshineBusy = false; await RefreshSunshineAsync(false); }
    }

    private async void BtnRefreshClients_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e) => await RefreshClientsAsync();

    private async void BtnDisconnectAll_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sunshineBusy) return;
        try { _sunshineBusy = true; SetManagedControls(false); await SunshineManager.DisconnectAllAsync(); }
        catch (Exception ex) { await Error(ex, "Sunshine"); }
        finally { _sunshineBusy = false; await RefreshSunshineAsync(); }
    }

    private async void BtnUnpairClient_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_sunshineBusy || ClientsListBox.SelectedItem is not SunshineClientInfo client) return;
        if (OwnerWindow == null || !await Dialogs.ConfirmAsync(OwnerWindow, "Sunshine", $"Unpair {client.DisplayName}?")) return;

        try { _sunshineBusy = true; SetManagedControls(false); await SunshineManager.UnpairAsync(client.Uuid); }
        catch (Exception ex) { await Error(ex, "Sunshine"); }
        finally { _sunshineBusy = false; await RefreshSunshineAsync(); }
    }

    private void ClientsListBox_SelectionChanged(object? s, SelectionChangedEventArgs e) =>
        BtnUnpairClient.IsEnabled = !_sunshineBusy && ClientsListBox.SelectedItem != null;

    private async void BtnOpenSunshineWebUi_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        try { await ExternalUrlLauncher.OpenAsync(this, "https://localhost:47990"); }
        catch (Exception ex) { await Error(ex, "Sunshine"); }
    }

    private void RefreshMoonlightInstallState()
    {
        var installed = OperatingSystem.IsWindows() && MoonlightManager.IsInstalled();
        if (!installed)
        {
            _moonlightReady = false;
            MoonlightInstallStatusText.Text = "Moonlight portable not found";
            MoonlightStatusDetailText.Text = "Download the Moonlight portable and place the Moonlight folder next to TeknoParrotUi.exe.";
            MoonlightPathText.Text = $"Expected: {MoonlightManager.MoonlightExecutablePath}";
            MoonlightPathText.IsVisible = true;
        }
        else
        {
            MoonlightInstallStatusText.Text = _moonlightReady ? "Ready" : "Stopped";
            MoonlightStatusDetailText.Text = _moonlightReady ? "Moonlight is enabled for TeknoParrot." : "Moonlight is installed and ready to enable.";
            MoonlightPathText.IsVisible = false;
        }

        BtnStartMoonlight.IsEnabled = installed && !_moonlightReady && !_moonlightBusy;
        BtnStopMoonlight.IsEnabled = installed && _moonlightReady && !_moonlightBusy;
        BtnOpenMoonlight.IsEnabled = installed && !_moonlightBusy;
        UpdateMoonlightControls();
    }

    private void UpdateMoonlightControls()
    {
        var enabled = OperatingSystem.IsWindows() && MoonlightManager.IsInstalled() && _moonlightReady && !_moonlightBusy;
        MoonlightHostTextBox.IsEnabled = BtnMoonlightPair.IsEnabled = BtnMoonlightRefreshApps.IsEnabled =
            BtnMoonlightQuitStream.IsEnabled = MoonlightAppsListBox.IsEnabled = enabled;
        BtnMoonlightStartStream.IsEnabled = enabled && MoonlightAppsListBox.SelectedItem != null &&
                                            !string.IsNullOrWhiteSpace(MoonlightHostTextBox.Text);
    }

    private void SetMoonlightBusy(bool value) { _moonlightBusy = value; RefreshMoonlightInstallState(); }

    private string Host()
    {
        var host = (MoonlightHostTextBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException("Enter a Moonlight host IP address or host name.");
        return host;
    }

    private static bool NotPaired(Exception ex) =>
        ex.Message.Contains("has not been paired", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("not paired", StringComparison.OrdinalIgnoreCase);

    private static bool HostUnavailable(Exception ex) =>
        new[] { "failed to connect", "timed out", "timeout", "connection refused", "actively refused", "unreachable" }
            .Any(x => ex.Message.Contains(x, StringComparison.OrdinalIgnoreCase));

    private void ApplyApps(string host, System.Collections.Generic.IReadOnlyList<string> apps)
    {
        MoonlightAppsListBox.ItemsSource = apps.Select(x => x.Equals("Desktop", StringComparison.OrdinalIgnoreCase) ? "Desktop - TeknoParrot" : x).ToList();
        MoonlightConnectionStatusText.Text = apps.Count == 0 ? $"Connected to {host}, but no applications were returned." : $"Connected to {host}.";
        UpdateMoonlightControls();
    }

    private async Task RefreshMoonlightHostStatusAsync()
    {
        if (_moonlightChecking || _moonlightBusy || !_moonlightReady || !MoonlightManager.IsInstalled()) return;
        var host = (MoonlightHostTextBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(host)) return;

        _moonlightChecking = true;
        try { ApplyApps(host, await MoonlightManager.ListAppsAsync(host, TimeSpan.FromSeconds(6))); }
        catch (Exception ex) when (NotPaired(ex))
        {
            MoonlightAppsListBox.ItemsSource = null;
            MoonlightGeneratedPinText.Text = "----";
            MoonlightPairStatusText.Text = "This host is not paired. Generate a new PIN to pair again.";
            MoonlightConnectionStatusText.Text = "Not Paired";
        }
        catch (Exception ex) when (HostUnavailable(ex))
        {
            MoonlightAppsListBox.ItemsSource = null;
            MoonlightConnectionStatusText.Text = "Host Offline / Sunshine Unavailable";
        }
        catch { }
        finally { _moonlightChecking = false; UpdateMoonlightControls(); }
    }

    private void BtnStartMoonlight_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (MoonlightManager.IsInstalled()) _moonlightReady = true;
        RefreshMoonlightInstallState();
    }

    private async void BtnStopMoonlight_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        _moonlightReady = false;
        try { MoonlightManager.StopAll(); } catch (Exception ex) { await Error(ex, "Moonlight"); }
        MoonlightAppsListBox.ItemsSource = null;
        MoonlightGeneratedPinText.Text = "----";
        MoonlightPairStatusText.Text = "Start pairing to generate a PIN, then enter that PIN on the Sunshine host.";
        MoonlightConnectionStatusText.Text = "Enter a host address to begin.";
        RefreshMoonlightInstallState();
    }

    private async void BtnOpenMoonlight_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        try { MoonlightManager.Open(); } catch (Exception ex) { await Error(ex, "Moonlight"); }
    }

    private async void BtnMoonlightPair_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_moonlightBusy) return;
        try
        {
            var host = Host();
            var pin = Random.Shared.Next(0, 10000).ToString("D4");
            MoonlightGeneratedPinText.Text = pin;
            MoonlightPairStatusText.Text = $"Enter PIN {pin} on the Sunshine host to approve this client.";
            SetMoonlightBusy(true);
            var result = await MoonlightManager.PairAsync(host, pin);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.GetBestError("Moonlight pairing failed."));
            MoonlightPairStatusText.Text = "Paired successfully.";
            MoonlightConnectionStatusText.Text = $"Paired with {host}.";
            _lastMoonlightCheck = DateTime.MinValue;
            await Task.Delay(750);
        }
        catch (Exception ex) { MoonlightPairStatusText.Text = "Pairing failed."; await Error(ex, "Moonlight"); }
        finally { SetMoonlightBusy(false); }
    }

    private async void BtnMoonlightRefreshApps_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_moonlightBusy) return;
        try { var host = Host(); SetMoonlightBusy(true); ApplyApps(host, await MoonlightManager.ListAppsAsync(host)); }
        catch (Exception ex) when (NotPaired(ex))
        {
            MoonlightAppsListBox.ItemsSource = null;
            MoonlightConnectionStatusText.Text = "Not Paired";
            MoonlightPairStatusText.Text = "This host is not paired. Generate a new PIN to pair again.";
        }
        catch (Exception ex) when (HostUnavailable(ex))
        {
            MoonlightAppsListBox.ItemsSource = null;
            MoonlightConnectionStatusText.Text = "Host Offline / Sunshine Unavailable";
        }
        catch (Exception ex) { await Error(ex, "Moonlight"); }
        finally { SetMoonlightBusy(false); }
    }

    private void MoonlightAppsListBox_SelectionChanged(object? s, SelectionChangedEventArgs e) => UpdateMoonlightControls();

    private void MoonlightHostTextBox_TextChanged(object? s, TextChangedEventArgs e)
    {
        var host = (MoonlightHostTextBox.Text ?? "").Trim();
        MoonlightAppsListBox.ItemsSource = null;
        _lastMoonlightCheck = DateTime.MinValue;
        MoonlightConnectionStatusText.Text = string.IsNullOrWhiteSpace(host) ? "Enter a host address to begin." : $"Target host: {host}";
        UpdateMoonlightControls();
    }

    private async void BtnMoonlightStartStream_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (!_moonlightReady) throw new InvalidOperationException("Enable Moonlight before launching a stream.");
            var host = Host();
            var selected = MoonlightAppsListBox.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(selected)) throw new InvalidOperationException("Select an application to stream.");
            MoonlightManager.StartStream(host, selected.Equals("Desktop - TeknoParrot", StringComparison.OrdinalIgnoreCase) ? "Desktop" : selected);
            MoonlightConnectionStatusText.Text = $"Streaming {selected} from {host}.";
        }
        catch (Exception ex) { await Error(ex, "Moonlight"); }
    }

    private async void BtnMoonlightQuitStream_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_moonlightBusy) return;
        try
        {
            var host = Host();
            SetMoonlightBusy(true);
            var result = await MoonlightManager.QuitStreamAsync(host);
            if (result.ExitCode != 0) throw new InvalidOperationException(result.GetBestError("Moonlight could not quit the remote application."));
            MoonlightConnectionStatusText.Text = $"Connected to {host}.";
        }
        catch (Exception ex) { await Error(ex, "Moonlight"); }
        finally { SetMoonlightBusy(false); }
    }

    private void BtnClose_Click(object? s, global::Avalonia.Interactivity.RoutedEventArgs e) => CloseRequested?.Invoke();

    private async Task Error(Exception ex, string title)
    {
        if (OwnerWindow != null) await Dialogs.InfoAsync(OwnerWindow, title, ex.Message);
    }
}
