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
    private readonly ReservedPortRangeService _reservedPortRangeService = new();
    private readonly ExternalLinkService _externalLinkService = new();
    private readonly ReleaseUpdateService _releaseUpdateService = new();
    private readonly UpdateInstallService _updateInstallService = new();
    private readonly BulkObservableCollection<PortEntry> _ports = [];
    private readonly BulkObservableCollection<ReservedPortRange> _reservedPortRanges = [];
    private readonly DispatcherTimer _searchDebounceTimer;
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _reservedPortRefreshCancellation;
    private string _searchText = string.Empty;
    private string _searchKeyword = string.Empty;
    private string _protocolFilter = "全部";
    private string _stateFilter = "全部";
    private PortEntry? _selectedPort;
    private ReservedPortRange? _selectedReservedPortRange;
    private string _reservedPortProtocol = "TCP";
    private string _reservedPortStore = "persistent";
    private string _reservedPortListStore = "active";
    private string _reservedPortStart = string.Empty;
    private string _reservedPortCount = "1";
    private bool _isRefreshingReservedPorts;
    private bool _isManagingReservedPort;
    private string _reservedPortStatusMessage = "保留端口尚未刷新";
    private string _emptyReservedPortMessage = string.Empty;
    private bool _isRefreshing;
    private string _statusMessage = "准备扫描端口";
    private string _scanStateText = "等待扫描";
    private string _portCountStatusText = "端口占用 --";
    private string _scanTotalDurationText = "总耗时 --";
    private string _scanPortDurationText = "端口扫描 --";
    private string _scanMetadataDurationText = "元数据 --";
    private string _scanProcessCountText = "进程 --";
    private string _permissionNotice = "普通权限：端口和 PID 可正常查看；部分系统进程详情可能受限，高风险操作会按需请求管理员权限。";
    private string _serviceOperationResult = string.Empty;
    private string _emptyPortListMessage = string.Empty;
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
        RefreshReservedPortsCommand = new AsyncRelayCommand(RefreshReservedPortsAsync, () => !IsRefreshingReservedPorts);
        AddReservedPortRangeCommand = new AsyncRelayCommand(AddReservedPortRangeAsync, () => CanAddReservedPortRange);
        DeleteReservedPortRangeCommand = new AsyncRelayCommand(DeleteReservedPortRangeAsync, () => CanDeleteReservedPortRange);
        KillProcessCommand = new AsyncRelayCommand(KillSelectedProcessAsync, () => CanKillSelectedProcess);
        StopServiceCommand = new AsyncRelayCommand(StopServiceAsync, CanControlService);
        RestartServiceCommand = new AsyncRelayCommand(RestartServiceAsync, CanControlService);
        OpenLocationCommand = new AsyncRelayCommand(OpenLocationAsync, () => !string.IsNullOrWhiteSpace(SelectedPort?.ProcessPath));
        OpenTaskManagerCommand = new AsyncRelayCommand(() => _processControlService.OpenTaskManagerAsync());
        OpenDeveloperHomeCommand = new AsyncRelayCommand(() => _externalLinkService.OpenUrlAsync(ApplicationInfo.DeveloperHomeUrl));
        OpenRepositoryCommand = new AsyncRelayCommand(() => _externalLinkService.OpenUrlAsync(ApplicationInfo.RepositoryUrl));
        CheckForUpdatesCommand = new AsyncRelayCommand(CheckForUpdatesAsync);
        ShowAboutCommand = new RelayCommand(ShowAbout);
        ShowLicenseCommand = new RelayCommand(ShowLicense);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => !string.IsNullOrWhiteSpace(SearchText));
    }

    public ICollectionView PortsView { get; }

    public IReadOnlyList<ReservedPortRange> ReservedPortRanges => _reservedPortRanges;

    public IReadOnlyList<string> ProtocolFilters { get; } = ["全部", "TCP", "UDP"];

    public IReadOnlyList<string> StateFilters { get; } = ["全部", "LISTENING", "BOUND", "ESTABLISHED", "TIME_WAIT", "UDP"];

    public IReadOnlyList<string> ReservedPortProtocols { get; } = ["TCP", "UDP"];

    public IReadOnlyList<string> ReservedPortListStores { get; } = ["active", "persistent"];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand RefreshReservedPortsCommand { get; }

    public AsyncRelayCommand AddReservedPortRangeCommand { get; }

    public AsyncRelayCommand DeleteReservedPortRangeCommand { get; }

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

    public AsyncRelayCommand OpenDeveloperHomeCommand { get; }

    public AsyncRelayCommand OpenRepositoryCommand { get; }

    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public RelayCommand ShowAboutCommand { get; }

    public RelayCommand ShowLicenseCommand { get; }

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

    public ReservedPortRange? SelectedReservedPortRange
    {
        get => _selectedReservedPortRange;
        set
        {
            if (SetProperty(ref _selectedReservedPortRange, value))
            {
                DeleteReservedPortRangeCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanDeleteReservedPortRange));
                OnPropertyChanged(nameof(SelectedReservedPortRangeSummary));
            }
        }
    }

    public string ReservedPortProtocol
    {
        get => _reservedPortProtocol;
        set
        {
            if (SetProperty(ref _reservedPortProtocol, value ?? "TCP"))
            {
                AddReservedPortRangeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ReservedPortStore
    {
        get => _reservedPortStore;
        set => SetProperty(ref _reservedPortStore, NormalizeReservedPortStore(value));
    }

    public string ReservedPortListStore
    {
        get => _reservedPortListStore;
        set
        {
            var normalizedValue = NormalizeReservedPortStore(value);
            if (SetProperty(ref _reservedPortListStore, normalizedValue))
            {
                _ = RefreshReservedPortsAsync();
            }
        }
    }

    public string ReservedPortStart
    {
        get => _reservedPortStart;
        set
        {
            if (SetProperty(ref _reservedPortStart, value ?? string.Empty))
            {
                AddReservedPortRangeCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanAddReservedPortRange));
            }
        }
    }

    public string ReservedPortCount
    {
        get => _reservedPortCount;
        set
        {
            if (SetProperty(ref _reservedPortCount, value ?? string.Empty))
            {
                AddReservedPortRangeCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanAddReservedPortRange));
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

    public bool IsRefreshingReservedPorts
    {
        get => _isRefreshingReservedPorts;
        private set
        {
            if (SetProperty(ref _isRefreshingReservedPorts, value))
            {
                RefreshReservedPortsCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanAddReservedPortRange));
            }
        }
    }

    public bool IsManagingReservedPort
    {
        get => _isManagingReservedPort;
        private set
        {
            if (SetProperty(ref _isManagingReservedPort, value))
            {
                AddReservedPortRangeCommand.RaiseCanExecuteChanged();
                DeleteReservedPortRangeCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanAddReservedPortRange));
                OnPropertyChanged(nameof(CanDeleteReservedPortRange));
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ScanStateText
    {
        get => _scanStateText;
        private set => SetProperty(ref _scanStateText, value);
    }

    public string PortCountStatusText
    {
        get => _portCountStatusText;
        private set => SetProperty(ref _portCountStatusText, value);
    }

    public string ScanTotalDurationText
    {
        get => _scanTotalDurationText;
        private set => SetProperty(ref _scanTotalDurationText, value);
    }

    public string ScanPortDurationText
    {
        get => _scanPortDurationText;
        private set => SetProperty(ref _scanPortDurationText, value);
    }

    public string ScanMetadataDurationText
    {
        get => _scanMetadataDurationText;
        private set => SetProperty(ref _scanMetadataDurationText, value);
    }

    public string ScanProcessCountText
    {
        get => _scanProcessCountText;
        private set => SetProperty(ref _scanProcessCountText, value);
    }

    public string ReservedPortStatusMessage
    {
        get => _reservedPortStatusMessage;
        private set => SetProperty(ref _reservedPortStatusMessage, value);
    }

    public string EmptyReservedPortMessage
    {
        get => _emptyReservedPortMessage;
        private set => SetProperty(ref _emptyReservedPortMessage, value);
    }

    public string PermissionNotice
    {
        get => _permissionNotice;
        private set => SetProperty(ref _permissionNotice, value);
    }

    public string ServiceOperationRiskText { get; } = "服务级操作只会控制选中的 Windows 服务，不会结束整个 svchost；停止或重启系统服务仍可能中断网络、登录、打印、更新等依赖功能。";

    public string EmptyPortListMessage
    {
        get => _emptyPortListMessage;
        private set => SetProperty(ref _emptyPortListMessage, value);
    }

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

    public int ReservedPortRangeCount => _reservedPortRanges.Count;

    public int AdministeredReservedPortRangeCount => _reservedPortRanges.Count(range => range.IsAdministered);

    public int FilteredCount => PortsView.Cast<object>().Count();

    public int SelectedServicesCount => SelectedPort?.Services.Count ?? 0;

    public bool CanAddReservedPortRange => !IsRefreshingReservedPorts
        && !IsManagingReservedPort
        && TryBuildReservedPortRangeRequest(out _, out _, out _, out _);

    public bool CanDeleteReservedPortRange => !IsManagingReservedPort && SelectedReservedPortRange is { IsAdministered: true };

    public string SelectedReservedPortRangeSummary => SelectedReservedPortRange is null
        ? "未选择保留端口"
        : SelectedReservedPortRange.IsAdministered
            ? SelectedReservedPortRange.DeleteTargetText
            : $"{SelectedReservedPortRange.DeleteTargetText}；系统保留范围不可直接删除";

    public string LastScannedText => LastScannedAt is null ? "尚未扫描" : LastScannedAt.Value.ToString("yyyy-MM-dd HH:mm:ss");

    public string ElevationText => IsElevated ? "管理员权限" : "普通权限 · 按需提权";

    public async Task InitializeAsync()
    {
        await Task.WhenAll(RefreshAsync(), RefreshReservedPortsAsync());
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
        ScanStateText = "正在扫描";
        PortCountStatusText = "端口占用 --";
        ScanTotalDurationText = "总耗时 --";
        ScanPortDurationText = "端口扫描 --";
        ScanMetadataDurationText = "元数据 --";
        ScanProcessCountText = "进程 --";
        EmptyPortListMessage = string.Empty;

        try
        {
            var result = await _portMonitorService.ScanAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var previousSelectedPort = SelectedPort;
            using (PortsView.DeferRefresh())
            {
                _ports.ReplaceAll(result.Entries);
            }

            LastScannedAt = result.ScannedAt;
            IsElevated = result.IsElevated;
            PermissionNotice = result.PermissionNotice ?? PermissionNotice;
            SelectVisiblePort(FindMatchingPort(previousSelectedPort));
            RaiseCountProperties();
            UpdateScanStatusDetails(result);

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
                UpdateEmptyPortListMessage();
            }

            currentCancellation.Dispose();
        }
    }

    private async Task RefreshReservedPortsAsync()
    {
        var previousCancellation = _reservedPortRefreshCancellation;
        var currentCancellation = new CancellationTokenSource();
        _reservedPortRefreshCancellation = currentCancellation;
        previousCancellation?.Cancel();

        var cancellationToken = currentCancellation.Token;

        IsRefreshingReservedPorts = true;
        EmptyReservedPortMessage = string.Empty;
        ReservedPortStatusMessage = $"正在读取 {GetStoreDisplayText(ReservedPortListStore)}保留端口...";

        try
        {
            var ranges = await _reservedPortRangeService.GetReservedRangesAsync(ReservedPortListStore, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var previousSelectedRange = SelectedReservedPortRange;
            _reservedPortRanges.ReplaceAll(ranges);
            SelectedReservedPortRange = FindMatchingReservedPortRange(previousSelectedRange) ?? _reservedPortRanges.FirstOrDefault();
            RaiseReservedPortProperties();

            ReservedPortStatusMessage = ranges.Count == 0
                ? $"{GetStoreDisplayText(ReservedPortListStore)}保留端口为空"
                : $"已读取 {ranges.Count} 条{GetStoreDisplayText(ReservedPortListStore)}保留端口，其中 {AdministeredReservedPortRangeCount} 条为用户保留";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_reservedPortRefreshCancellation, currentCancellation))
            {
                ReservedPortStatusMessage = "保留端口刷新已取消";
            }
        }
        catch (Exception exception)
        {
            ReservedPortStatusMessage = $"保留端口读取失败：{exception.Message}";
            _reservedPortRanges.ReplaceAll([]);
            SelectedReservedPortRange = null;
            RaiseReservedPortProperties();
        }
        finally
        {
            if (ReferenceEquals(_reservedPortRefreshCancellation, currentCancellation))
            {
                IsRefreshingReservedPorts = false;
                _reservedPortRefreshCancellation = null;
                UpdateEmptyReservedPortMessage();
            }

            currentCancellation.Dispose();
        }
    }

    private async Task AddReservedPortRangeAsync()
    {
        if (!TryBuildReservedPortRangeRequest(out var protocol, out var startPort, out var portCount, out var store))
        {
            MessageBox.Show("请输入有效的协议、起始端口和端口数量。", "保留端口", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var endPort = startPort + portCount - 1;
        var result = MessageBox.Show(
            $"确定要添加 {protocol.ToString().ToUpperInvariant()} {FormatPortRange(startPort, endPort)} 到 Windows 保留端口吗？{Environment.NewLine}{Environment.NewLine}存储范围：{GetStoreDisplayText(store)}。添加后其他程序将不能绑定该范围。",
            "添加保留端口",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await ManageReservedPortRangeAsync(
            "添加",
            () => _reservedPortRangeService.AddReservedRangeAsync(protocol, startPort, portCount, store, CancellationToken.None),
            () => _reservedPortRangeService.AddReservedRangeElevatedAsync(protocol, startPort, portCount, store, CancellationToken.None),
            $"{protocol.ToString().ToUpperInvariant()} {FormatPortRange(startPort, endPort)}（{GetStoreDisplayText(store)}）",
            store);
    }

    private async Task DeleteReservedPortRangeAsync()
    {
        if (SelectedReservedPortRange is null)
        {
            return;
        }

        var selected = SelectedReservedPortRange;
        if (!selected.IsAdministered)
        {
            MessageBox.Show(
                "该范围由系统动态保留，未标记为用户保留；请只删除带用户保留标记的规则。",
                "删除保留端口",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"确定要删除 Windows 保留端口 {selected.DeleteTargetText} 吗？{Environment.NewLine}{Environment.NewLine}删除时必须与原规则的起始端口和端口数量完全一致。",
            "删除保留端口",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await ManageReservedPortRangeAsync(
            "删除",
            () => _reservedPortRangeService.DeleteReservedRangeAsync(selected, CancellationToken.None),
            () => _reservedPortRangeService.DeleteReservedRangeElevatedAsync(selected, CancellationToken.None),
            selected.DeleteTargetText,
            selected.Store);
    }

    private async Task ManageReservedPortRangeAsync(
        string actionText,
        Func<Task> operation,
        Func<Task> elevatedOperation,
        string targetText,
        string refreshStore)
    {
        IsManagingReservedPort = true;

        try
        {
            ReservedPortStatusMessage = $"正在{actionText}保留端口 {targetText}...";
            await operation();
            ReservedPortStatusMessage = $"已{actionText}保留端口 {targetText}，正在刷新列表...";
            await RefreshReservedPortsForStoreAsync(refreshStore);
            ReservedPortStatusMessage = $"已{actionText}保留端口 {targetText}。";
        }
        catch (ProcessControlException exception) when (exception.CanRetryElevated)
        {
            var elevateResult = MessageBox.Show(
                $"{exception.Message}{Environment.NewLine}{Environment.NewLine}是否以管理员权限重试{actionText}保留端口 {targetText}？",
                "需要管理员权限",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (elevateResult != MessageBoxResult.Yes)
            {
                ReservedPortStatusMessage = $"{actionText}保留端口受限：{exception.Message}";
                return;
            }

            try
            {
                ReservedPortStatusMessage = $"正在请求管理员权限{actionText}保留端口 {targetText}...";
                await elevatedOperation();
                ReservedPortStatusMessage = $"已通过管理员权限{actionText}保留端口 {targetText}，正在刷新列表...";
                await RefreshReservedPortsForStoreAsync(refreshStore);
                ReservedPortStatusMessage = $"已通过管理员权限{actionText}保留端口 {targetText}。";
            }
            catch (Exception elevatedException)
            {
                ReservedPortStatusMessage = $"管理员权限{actionText}保留端口失败：{elevatedException.Message}";
                MessageBox.Show(ReservedPortStatusMessage, "保留端口操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception exception)
        {
            ReservedPortStatusMessage = $"{actionText}保留端口失败：{exception.Message}";
            MessageBox.Show(ReservedPortStatusMessage, "保留端口操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsManagingReservedPort = false;
            UpdateEmptyReservedPortMessage();
        }
    }

    private Task RefreshReservedPortsForStoreAsync(string store)
    {
        var normalizedStore = NormalizeReservedPortStore(store);
        if (!ReservedPortListStore.Equals(normalizedStore, StringComparison.OrdinalIgnoreCase))
        {
            SetProperty(ref _reservedPortListStore, normalizedStore, nameof(ReservedPortListStore));
        }

        return RefreshReservedPortsAsync();
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

    private static void ShowAbout()
    {
        MessageBox.Show(
            $"{ApplicationInfo.Name}{Environment.NewLine}" +
            $"版本：{ApplicationInfo.CurrentVersionText}{Environment.NewLine}" +
            $"开发者：{ApplicationInfo.DeveloperName}{Environment.NewLine}" +
            $"仓库：{ApplicationInfo.RepositoryUrl}{Environment.NewLine}" +
            $"许可证：{ApplicationInfo.LicenseName}{Environment.NewLine}{Environment.NewLine}" +
            ApplicationInfo.LicenseDescription,
            $"关于 {ApplicationInfo.Name}",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void ShowLicense()
    {
        MessageBox.Show(
            $"许可证：{ApplicationInfo.LicenseName}{Environment.NewLine}{Environment.NewLine}" +
            ApplicationInfo.LicenseDescription,
            "许可证",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async Task CheckForUpdatesAsync()
    {
        StatusMessage = "正在检查 GitHub Release 更新...";

        try
        {
            var result = await _releaseUpdateService.CheckLatestReleaseAsync(CancellationToken.None);
            await ShowUpdateCheckResultAsync(result);
        }
        catch (Exception exception)
        {
            StatusMessage = $"检查更新失败：{exception.Message}";
            MessageBox.Show(
                StatusMessage,
                "检查更新",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task ShowUpdateCheckResultAsync(UpdateCheckResult result)
    {
        switch (result.State)
        {
            case UpdateCheckState.NoRelease:
                StatusMessage = "检查更新完成：仓库暂无可用 Release。";
                MessageBox.Show(
                    $"当前 GitHub 仓库还没有可用 Release。{Environment.NewLine}{Environment.NewLine}当前版本：{result.CurrentVersionText}",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;

            case UpdateCheckState.VersionUnknown:
                StatusMessage = $"检查更新完成：找到 Release {result.LatestVersionText}。";
                await PromptOpenReleaseAsync(
                    result,
                    $"已找到最新 Release：{BuildReleaseTitle(result)}，但无法识别版本号。{Environment.NewLine}{Environment.NewLine}" +
                    $"当前版本：{result.CurrentVersionText}{Environment.NewLine}" +
                    "是否打开 Release 页面？",
                    MessageBoxImage.Information);
                return;

            case UpdateCheckState.UpdateAvailable:
                StatusMessage = $"发现新版本 {result.LatestVersionText}，当前版本 {result.CurrentVersionText}。";
                await PromptInstallUpdateAsync(result);
                return;

            case UpdateCheckState.UpToDate:
                StatusMessage = $"当前已是最新版本 {result.CurrentVersionText}。";
                MessageBox.Show(
                    $"当前已是最新版本。{Environment.NewLine}{Environment.NewLine}" +
                    $"当前版本：{result.CurrentVersionText}{Environment.NewLine}" +
                    $"最新 Release：{BuildReleaseTitle(result)}",
                    "检查更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
        }
    }

    private async Task PromptOpenReleaseAsync(UpdateCheckResult result, string message, MessageBoxImage icon)
    {
        var response = MessageBox.Show(
            message,
            "检查更新",
            MessageBoxButton.YesNo,
            icon,
            MessageBoxResult.Yes);

        if (response == MessageBoxResult.Yes && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
        {
            await _externalLinkService.OpenUrlAsync(result.ReleaseUrl);
        }
    }

    private async Task PromptInstallUpdateAsync(UpdateCheckResult result)
    {
        var response = MessageBox.Show(
            $"发现新版本：{BuildReleaseTitle(result)}{Environment.NewLine}{Environment.NewLine}" +
            $"当前版本：{result.CurrentVersionText}{Environment.NewLine}" +
            $"最新版本：{result.LatestVersionText}{Environment.NewLine}{Environment.NewLine}" +
            "选择“是”将自动下载并覆盖旧版本，完成后会重启程序。" +
            $"{Environment.NewLine}选择“否”只打开 Release 页面。",
            "检查更新",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (response == MessageBoxResult.Yes)
        {
            await InstallUpdateAsync(result);
            return;
        }

        if (response == MessageBoxResult.No && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
        {
            await _externalLinkService.OpenUrlAsync(result.ReleaseUrl);
        }
    }

    private async Task InstallUpdateAsync(UpdateCheckResult result)
    {
        try
        {
            var progress = new Progress<string>(message => StatusMessage = message);
            var launchResult = await _updateInstallService.DownloadAndLaunchAsync(
                result,
                progress,
                CancellationToken.None);

            StatusMessage = $"已准备更新包 {launchResult.AssetName}，正在关闭以覆盖旧版本...";
            MessageBox.Show(
                $"更新包已下载并校验完成。{Environment.NewLine}{Environment.NewLine}" +
                "程序将关闭，随后自动覆盖旧版本并重启。" +
                $"{(launchResult.RequiresElevation ? $"{Environment.NewLine}{Environment.NewLine}安装位置需要管理员权限，请确认系统弹出的权限请求。" : string.Empty)}" +
                $"{Environment.NewLine}{Environment.NewLine}更新日志：{launchResult.LogPath}",
                "安装更新",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Application.Current.Shutdown();
        }
        catch (Exception exception)
        {
            StatusMessage = $"自动更新失败：{exception.Message}";
            MessageBox.Show(
                $"{StatusMessage}{Environment.NewLine}{Environment.NewLine}你仍然可以在 Release 页面手动下载更新包。",
                "安装更新",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private static string BuildReleaseTitle(UpdateCheckResult result)
    {
        var title = !string.IsNullOrWhiteSpace(result.ReleaseName)
            ? result.ReleaseName
            : result.LatestVersionText ?? "未知版本";

        return result.PublishedAt is null
            ? title
            : $"{title}（{result.PublishedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm}）";
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
        var metrics = result.Metrics;
        var countText = _ports.Count == 0
            ? "扫描完成，但未获取到端口记录"
            : FilteredCount == _ports.Count
                ? $"扫描完成，发现 {_ports.Count} 个端口占用"
                : $"扫描完成，发现 {_ports.Count} 个端口占用，当前筛选显示 {FilteredCount} 条";

        if (result.Warning is not null)
        {
            return $"{countText}；{result.Warning}。{PermissionNotice}";
        }

        return $"{countText}（总耗时 {metrics.TotalDuration.TotalMilliseconds:F0}ms，端口扫描 {metrics.PortSnapshotDuration.TotalMilliseconds:F0}ms，元数据 {metrics.MetadataDuration.TotalMilliseconds:F0}ms，进程 {metrics.DistinctProcessCount}）。{PermissionNotice}";
    }

    private void UpdateScanStatusDetails(PortScanResult result)
    {
        var metrics = result.Metrics;
        ScanStateText = result.Warning is null ? "扫描完成" : "扫描完成，存在提示";
        PortCountStatusText = $"端口占用 {_ports.Count} 个";
        ScanTotalDurationText = $"总耗时 {metrics.TotalDuration.TotalMilliseconds:F0}ms";
        ScanPortDurationText = $"端口扫描 {metrics.PortSnapshotDuration.TotalMilliseconds:F0}ms";
        ScanMetadataDurationText = $"元数据 {metrics.MetadataDuration.TotalMilliseconds:F0}ms";
        ScanProcessCountText = $"进程 {metrics.DistinctProcessCount} 个";
    }

    private void SearchDebounceTimerTick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        PortsView.Refresh();
        SelectVisiblePort();
        OnPropertyChanged(nameof(FilteredCount));
        UpdateEmptyPortListMessage();
    }

    private void UpdateEmptyPortListMessage()
    {
        EmptyPortListMessage = (IsRefreshing, _ports.Count, FilteredCount) switch
        {
            (true, _, _) => string.Empty,
            (false, 0, _) => "未获取到端口记录。请确认系统网络服务正在运行，或尝试以管理员权限运行后刷新。",
            (false, > 0, 0) => "当前筛选没有匹配端口。清除搜索或切换筛选条件后再查看。",
            _ => string.Empty
        };
    }

    private void UpdateEmptyReservedPortMessage()
    {
        EmptyReservedPortMessage = (IsRefreshingReservedPorts, _reservedPortRanges.Count) switch
        {
            (true, _) => string.Empty,
            (false, 0) => $"{GetStoreDisplayText(ReservedPortListStore)}保留端口为空，或当前权限无法读取。点击刷新可重新获取。",
            _ => string.Empty
        };
    }

    private PortEntry? FindMatchingPort(PortEntry? port)
    {
        if (port is null)
        {
            return null;
        }

        return _ports.FirstOrDefault(candidate =>
            candidate.Protocol == port.Protocol
            && candidate.LocalAddress.Equals(port.LocalAddress, StringComparison.OrdinalIgnoreCase)
            && candidate.LocalPort == port.LocalPort
            && candidate.RemoteAddress.Equals(port.RemoteAddress, StringComparison.OrdinalIgnoreCase)
            && candidate.RemotePort == port.RemotePort
            && candidate.ProcessId == port.ProcessId);
    }

    private ReservedPortRange? FindMatchingReservedPortRange(ReservedPortRange? range)
    {
        if (range is null)
        {
            return null;
        }

        return _reservedPortRanges.FirstOrDefault(candidate =>
            candidate.Protocol == range.Protocol
            && candidate.StartPort == range.StartPort
            && candidate.EndPort == range.EndPort
            && candidate.Store.Equals(range.Store, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryBuildReservedPortRangeRequest(
        out PortProtocol protocol,
        out int startPort,
        out int portCount,
        out string store)
    {
        protocol = PortProtocol.Tcp;
        startPort = 0;
        portCount = 0;
        store = NormalizeReservedPortStore(ReservedPortStore);

        if (ReservedPortProtocol.Equals("UDP", StringComparison.OrdinalIgnoreCase))
        {
            protocol = PortProtocol.Udp;
        }
        else if (!ReservedPortProtocol.Equals("TCP", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(ReservedPortStart, out startPort)
            || !int.TryParse(ReservedPortCount, out portCount)
            || startPort is < 0 or > 65535
            || portCount <= 0
            || startPort + portCount - 1 > 65535)
        {
            return false;
        }

        return true;
    }

    private void SelectVisiblePort(PortEntry? preferredPort = null)
    {
        var targetPort = preferredPort ?? SelectedPort;
        PortEntry? firstVisiblePort = null;
        var targetPortVisible = false;

        foreach (var item in PortsView)
        {
            if (item is not PortEntry port)
            {
                continue;
            }

            firstVisiblePort ??= port;
            if (ReferenceEquals(port, targetPort))
            {
                targetPortVisible = true;
                break;
            }
        }

        SelectedPort = targetPortVisible ? targetPort : firstVisiblePort;
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

    private void RaiseReservedPortProperties()
    {
        OnPropertyChanged(nameof(ReservedPortRangeCount));
        OnPropertyChanged(nameof(AdministeredReservedPortRangeCount));
        OnPropertyChanged(nameof(CanAddReservedPortRange));
        OnPropertyChanged(nameof(CanDeleteReservedPortRange));
        OnPropertyChanged(nameof(SelectedReservedPortRangeSummary));
    }

    private static string NormalizeReservedPortStore(string? store)
    {
        return string.Equals(store, "active", StringComparison.OrdinalIgnoreCase)
            ? "active"
            : "persistent";
    }

    private static string GetStoreDisplayText(string store)
    {
        return store.Equals("persistent", StringComparison.OrdinalIgnoreCase)
            ? "持久"
            : "当前";
    }

    private static string FormatPortRange(int startPort, int endPort)
    {
        return startPort == endPort
            ? startPort.ToString()
            : $"{startPort}-{endPort}";
    }
}
