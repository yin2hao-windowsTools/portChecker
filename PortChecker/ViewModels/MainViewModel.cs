using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using PortChecker.Models;
using PortChecker.Services;

namespace PortChecker.ViewModels;

internal sealed class MainViewModel : ObservableObject
{
    private readonly PortMonitorService _portMonitorService = new();
    private readonly ProcessControlService _processControlService = new();
    private readonly BulkObservableCollection<PortEntry> _ports = [];
    private readonly DispatcherTimer _searchDebounceTimer;
    private CancellationTokenSource? _refreshCancellation;
    private string _searchText = string.Empty;
    private string _searchKeyword = string.Empty;
    private string _protocolFilter = "全部";
    private string _stateFilter = "全部";
    private PortEntry? _selectedPort;
    private bool _isRefreshing;
    private string _statusMessage = "准备扫描端口";
    private string _permissionNotice = "普通权限：端口和 PID 可正常查看；部分系统进程详情可能受限，高风险操作会按需请求管理员权限。";
    private string _serviceOperationResult = string.Empty;
    private bool _isControllingService;
    private DateTimeOffset? _lastScannedAt;
    private bool _isElevated;

    public MainViewModel()
    {
        _searchDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _searchDebounceTimer.Tick += SearchDebounceTimerTick;

        PortsView = CollectionViewSource.GetDefaultView(_ports);
        PortsView.Filter = FilterPort;
        PortsView.SortDescriptions.Add(new SortDescription(nameof(PortEntry.LocalPort), ListSortDirection.Ascending));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsRefreshing);
        KillProcessCommand = new AsyncRelayCommand(KillSelectedProcessAsync, () => CanKillSelectedProcess);
        StopServiceCommand = new AsyncRelayCommand(StopServiceAsync, CanControlService);
        RestartServiceCommand = new AsyncRelayCommand(RestartServiceAsync, CanControlService);
        OpenLocationCommand = new AsyncRelayCommand(OpenLocationAsync, () => !string.IsNullOrWhiteSpace(SelectedPort?.ProcessPath));
        OpenTaskManagerCommand = new AsyncRelayCommand(() => _processControlService.OpenTaskManagerAsync());
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => !string.IsNullOrWhiteSpace(SearchText));
    }

    public ICollectionView PortsView { get; }

    public IReadOnlyList<string> ProtocolFilters { get; } = ["全部", "TCP", "UDP"];

    public IReadOnlyList<string> StateFilters { get; } = ["全部", "LISTENING", "ESTABLISHED", "TIME_WAIT", "UDP"];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand KillProcessCommand { get; }

    public AsyncRelayCommand StopServiceCommand { get; }

    public AsyncRelayCommand RestartServiceCommand { get; }

    public AsyncRelayCommand OpenLocationCommand { get; }

    public bool CanKillSelectedProcess => SelectedPort is { ProcessId: > 0, IsSvchost: false };

    public string KillProcessWarningText => SelectedPort is { IsSvchost: true }
        ? SelectedPort.Services.Count > 0
            ? "该 PID 是 svchost，请优先停止或重启下方具体服务，避免影响同一宿主中的其他服务。"
            : "该 PID 是 svchost，但当前未解析到服务；为避免误杀宿主进程，已禁用结束进程。请使用管理员权限刷新后按服务操作。"
        : string.Empty;

    public AsyncRelayCommand OpenTaskManagerCommand { get; }

    public RelayCommand ClearSearchCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                _searchKeyword = _searchText.Trim();
                ClearSearchCommand.RaiseCanExecuteChanged();
                _searchDebounceTimer.Stop();

                if (_searchKeyword.Length == 0)
                {
                    ApplyFilter();
                    return;
                }

                _searchDebounceTimer.Start();
            }
        }
    }

    public string ProtocolFilter
    {
        get => _protocolFilter;
        set
        {
            if (SetProperty(ref _protocolFilter, value))
            {
                ApplyFilter();
            }
        }
    }

    public string StateFilter
    {
        get => _stateFilter;
        set
        {
            if (SetProperty(ref _stateFilter, value))
            {
                ApplyFilter();
            }
        }
    }

    public PortEntry? SelectedPort
    {
        get => _selectedPort;
        set
        {
            if (SetProperty(ref _selectedPort, value))
            {
                KillProcessCommand.RaiseCanExecuteChanged();
                OpenLocationCommand.RaiseCanExecuteChanged();
                StopServiceCommand.RaiseCanExecuteChanged();
                RestartServiceCommand.RaiseCanExecuteChanged();
                ServiceOperationResult = string.Empty;
                OnPropertyChanged(nameof(SelectedServicesCount));
                OnPropertyChanged(nameof(CanKillSelectedProcess));
                OnPropertyChanged(nameof(KillProcessWarningText));
            }
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string PermissionNotice
    {
        get => _permissionNotice;
        private set => SetProperty(ref _permissionNotice, value);
    }

    public string ServiceOperationRiskText { get; } = "服务级操作只会控制选中的 Windows 服务，不会结束整个 svchost；停止或重启系统服务仍可能中断网络、登录、打印、更新等依赖功能。";

    public string ServiceOperationResult
    {
        get => _serviceOperationResult;
        private set => SetProperty(ref _serviceOperationResult, value);
    }

    private bool IsControllingService
    {
        get => _isControllingService;
        set
        {
            if (SetProperty(ref _isControllingService, value))
            {
                StopServiceCommand.RaiseCanExecuteChanged();
                RestartServiceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public DateTimeOffset? LastScannedAt
    {
        get => _lastScannedAt;
        private set
        {
            if (SetProperty(ref _lastScannedAt, value))
            {
                OnPropertyChanged(nameof(LastScannedText));
            }
        }
    }

    public bool IsElevated
    {
        get => _isElevated;
        private set
        {
            if (SetProperty(ref _isElevated, value))
            {
                OnPropertyChanged(nameof(ElevationText));
            }
        }
    }

    public int TotalCount => _ports.Count;

    public int TcpCount => _ports.Count(port => port.Protocol == PortProtocol.Tcp);

    public int UdpCount => _ports.Count(port => port.Protocol == PortProtocol.Udp);

    public int SvchostCount => _ports.Count(port => port.IsSvchost);

    public int FilteredCount => PortsView.Cast<object>().Count();

    public int SelectedServicesCount => SelectedPort?.Services.Count ?? 0;

    public string LastScannedText => LastScannedAt is null ? "尚未扫描" : LastScannedAt.Value.ToString("yyyy-MM-dd HH:mm:ss");

    public string ElevationText => IsElevated ? "管理员权限" : "普通权限 - 按需提权";

    public async Task InitializeAsync()
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        var previousCancellation = _refreshCancellation;
        var currentCancellation = new CancellationTokenSource();
        _refreshCancellation = currentCancellation;
        previousCancellation?.Cancel();

        var cancellationToken = currentCancellation.Token;

        IsRefreshing = true;
        StatusMessage = "正在扫描端口和进程信息...";

        try
        {
            var result = await _portMonitorService.ScanAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            using (PortsView.DeferRefresh())
            {
                _ports.ReplaceAll(result.Entries);
                SelectedPort = _ports.FirstOrDefault();
            }

            LastScannedAt = result.ScannedAt;
            IsElevated = result.IsElevated;
            PermissionNotice = result.PermissionNotice ?? PermissionNotice;
            PortsView.Refresh();
            RaiseCountProperties();

            StatusMessage = BuildScanStatusMessage(result);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_refreshCancellation, currentCancellation))
            {
                StatusMessage = "扫描已取消";
            }
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, currentCancellation))
            {
                IsRefreshing = false;
                _refreshCancellation = null;
            }

            currentCancellation.Dispose();
        }
    }

    private async Task KillSelectedProcessAsync()
    {
        if (SelectedPort is null)
        {
            return;
        }

        var selected = SelectedPort;
        if (selected.IsSvchost)
        {
            MessageBox.Show(
                KillProcessWarningText,
                "请按服务操作",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"确定要结束进程 {selected.ProcessName} (PID {selected.ProcessId}) 吗？",
            "结束进程",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            StatusMessage = $"正在结束 PID {selected.ProcessId}...";
            await _processControlService.KillProcessAsync(selected.ProcessId, CancellationToken.None);
            StatusMessage = $"已结束 PID {selected.ProcessId}，正在刷新...";
            await RefreshAsync();
        }
        catch (ProcessControlException exception) when (exception.CanRetryElevated)
        {
            var elevateResult = MessageBox.Show(
                $"{exception.Message}{Environment.NewLine}{Environment.NewLine}是否以管理员权限重试结束 PID {selected.ProcessId}？",
                "需要管理员权限",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (elevateResult != MessageBoxResult.Yes)
            {
                StatusMessage = $"结束进程受限：{exception.Message}";
                return;
            }

            try
            {
                StatusMessage = $"正在请求管理员权限结束 PID {selected.ProcessId}...";
                await _processControlService.KillProcessElevatedAsync(selected.ProcessId, CancellationToken.None);
                StatusMessage = $"已结束 PID {selected.ProcessId}，正在刷新...";
                await RefreshAsync();
            }
            catch (Exception elevatedException)
            {
                StatusMessage = $"管理员权限结束进程失败：{elevatedException.Message}";
                MessageBox.Show(StatusMessage, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception exception)
        {
            StatusMessage = $"结束进程失败：{exception.Message}";
            MessageBox.Show(StatusMessage, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task StopServiceAsync(object? parameter)
    {
        if (parameter is not ServiceInfo service)
        {
            return;
        }

        var result = MessageBox.Show(
            $"确定要停止服务 {service.DisplayLabel} 吗？{Environment.NewLine}{Environment.NewLine}只会向该服务发送停止请求，不会结束承载它的 svchost 进程。",
            "停止服务",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await ControlServiceAsync(
            service,
            "停止",
            token => _processControlService.StopServiceAsync(service.Name, token),
            token => _processControlService.StopServiceElevatedAsync(service.Name, token));
    }

    private async Task RestartServiceAsync(object? parameter)
    {
        if (parameter is not ServiceInfo service)
        {
            return;
        }

        var result = MessageBox.Show(
            $"确定要重启服务 {service.DisplayLabel} 吗？{Environment.NewLine}{Environment.NewLine}会先停止该服务，等待停止完成后再启动；不会结束承载它的 svchost 进程。",
            "重启服务",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await ControlServiceAsync(
            service,
            "重启",
            token => _processControlService.RestartServiceAsync(service.Name, token),
            token => _processControlService.RestartServiceElevatedAsync(service.Name, token));
    }

    private async Task ControlServiceAsync(
        ServiceInfo service,
        string actionText,
        Func<CancellationToken, Task> operation,
        Func<CancellationToken, Task> elevatedOperation)
    {
        IsControllingService = true;

        try
        {
            ServiceOperationResult = string.Empty;
            StatusMessage = $"正在{actionText}服务 {service.Name}...";
            await operation(CancellationToken.None);
            var completedMessage = $"已{actionText}服务 {service.DisplayLabel}，正在刷新状态...";
            StatusMessage = completedMessage;
            ServiceOperationResult = completedMessage;
            await RefreshAsync();
            ServiceOperationResult = $"已{actionText}服务 {service.DisplayLabel}；列表已刷新，请核对服务状态。";
            StatusMessage = ServiceOperationResult;
        }
        catch (ProcessControlException exception) when (exception.CanRetryElevated)
        {
            var elevateResult = MessageBox.Show(
                $"{exception.Message}{Environment.NewLine}{Environment.NewLine}是否以管理员权限重试{actionText}服务 {service.DisplayLabel}？{Environment.NewLine}该操作仍只作用于这个服务，不会结束 svchost 进程。",
                "需要管理员权限",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (elevateResult != MessageBoxResult.Yes)
            {
                ServiceOperationResult = $"{actionText}服务 {service.DisplayLabel} 受限：{exception.Message}";
                StatusMessage = ServiceOperationResult;
                return;
            }

            try
            {
                StatusMessage = $"正在请求管理员权限{actionText}服务 {service.Name}...";
                ServiceOperationResult = StatusMessage;
                await elevatedOperation(CancellationToken.None);
                ServiceOperationResult = $"已通过管理员权限{actionText}服务 {service.DisplayLabel}，正在刷新状态...";
                StatusMessage = ServiceOperationResult;
                await RefreshAsync();
                ServiceOperationResult = $"已通过管理员权限{actionText}服务 {service.DisplayLabel}；列表已刷新，请核对服务状态。";
                StatusMessage = ServiceOperationResult;
            }
            catch (Exception elevatedException)
            {
                ServiceOperationResult = $"管理员权限{actionText}服务 {service.DisplayLabel} 失败：{elevatedException.Message}";
                StatusMessage = ServiceOperationResult;
                MessageBox.Show(ServiceOperationResult, "服务操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception exception)
        {
            ServiceOperationResult = $"{actionText}服务 {service.DisplayLabel} 失败：{exception.Message}";
            StatusMessage = ServiceOperationResult;
            MessageBox.Show(ServiceOperationResult, "服务操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsControllingService = false;
        }
    }

    private async Task OpenLocationAsync()
    {
        if (SelectedPort?.ProcessPath is null)
        {
            return;
        }

        await _processControlService.OpenFileLocationAsync(SelectedPort.ProcessPath);
    }

    private bool FilterPort(object item)
    {
        if (item is not PortEntry port)
        {
            return false;
        }

        if (ProtocolFilter != "全部" && !port.ProtocolText.Equals(ProtocolFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (StateFilter != "全部")
        {
            if (StateFilter == "UDP" && port.Protocol != PortProtocol.Udp)
            {
                return false;
            }

            if (StateFilter != "UDP" && !port.State.Equals(StateFilter, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return port.SearchIndex.Contains(_searchKeyword, StringComparison.OrdinalIgnoreCase);
    }

    private bool CanControlService(object? parameter)
    {
        return !IsControllingService && parameter is ServiceInfo { CanControl: true, IsRunning: true };
    }

    private string BuildScanStatusMessage(PortScanResult result)
    {
        if (result.Warning is not null)
        {
            return $"{result.Warning}。{PermissionNotice}";
        }

        var metrics = result.Metrics;
        return $"扫描完成，发现 {_ports.Count} 个端口占用（总耗时 {metrics.TotalDuration.TotalMilliseconds:F0}ms，端口扫描 {metrics.PortSnapshotDuration.TotalMilliseconds:F0}ms，元数据 {metrics.MetadataDuration.TotalMilliseconds:F0}ms，进程 {metrics.DistinctProcessCount}）。{PermissionNotice}";
    }

    private void SearchDebounceTimerTick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        PortsView.Refresh();
        OnPropertyChanged(nameof(FilteredCount));
    }

    private void RaiseCountProperties()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(TcpCount));
        OnPropertyChanged(nameof(UdpCount));
        OnPropertyChanged(nameof(SvchostCount));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(SelectedServicesCount));
        OnPropertyChanged(nameof(CanKillSelectedProcess));
        OnPropertyChanged(nameof(KillProcessWarningText));
    }
}
