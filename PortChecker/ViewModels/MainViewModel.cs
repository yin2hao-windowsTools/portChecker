using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using PortChecker.Models;
using PortChecker.Services;

namespace PortChecker.ViewModels;

internal sealed class MainViewModel : ObservableObject
{
    private readonly PortMonitorService _portMonitorService = new();
    private readonly ProcessControlService _processControlService = new();
    private readonly ObservableCollection<PortEntry> _ports = [];
    private CancellationTokenSource? _refreshCancellation;
    private string _searchText = string.Empty;
    private string _protocolFilter = "全部";
    private string _stateFilter = "全部";
    private PortEntry? _selectedPort;
    private bool _isRefreshing;
    private string _statusMessage = "准备扫描端口";
    private string _permissionNotice = "普通权限：端口和 PID 可正常查看；部分系统进程详情可能受限，高风险操作会按需请求管理员权限。";
    private DateTimeOffset? _lastScannedAt;
    private bool _isElevated;

    public MainViewModel()
    {
        PortsView = CollectionViewSource.GetDefaultView(_ports);
        PortsView.Filter = FilterPort;
        PortsView.SortDescriptions.Add(new SortDescription(nameof(PortEntry.LocalPort), ListSortDirection.Ascending));

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsRefreshing);
        KillProcessCommand = new AsyncRelayCommand(KillSelectedProcessAsync, () => SelectedPort is not null && SelectedPort.ProcessId > 0);
        OpenLocationCommand = new AsyncRelayCommand(OpenLocationAsync, () => !string.IsNullOrWhiteSpace(SelectedPort?.ProcessPath));
        OpenTaskManagerCommand = new AsyncRelayCommand(() => _processControlService.OpenTaskManagerAsync());
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty, () => !string.IsNullOrWhiteSpace(SearchText));
    }

    public ICollectionView PortsView { get; }

    public IReadOnlyList<string> ProtocolFilters { get; } = ["全部", "TCP", "UDP"];

    public IReadOnlyList<string> StateFilters { get; } = ["全部", "LISTENING", "ESTABLISHED", "TIME_WAIT", "UDP"];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand KillProcessCommand { get; }

    public AsyncRelayCommand OpenLocationCommand { get; }

    public AsyncRelayCommand OpenTaskManagerCommand { get; }

    public RelayCommand ClearSearchCommand { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                PortsView.Refresh();
                ClearSearchCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(FilteredCount));
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
                PortsView.Refresh();
                OnPropertyChanged(nameof(FilteredCount));
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
                PortsView.Refresh();
                OnPropertyChanged(nameof(FilteredCount));
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
                OnPropertyChanged(nameof(SelectedServicesCount));
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
        _refreshCancellation?.Cancel();
        _refreshCancellation = new CancellationTokenSource();
        var cancellationToken = _refreshCancellation.Token;

        IsRefreshing = true;
        StatusMessage = "正在扫描端口和进程信息...";

        try
        {
            var result = await _portMonitorService.ScanAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _ports.Clear();
            foreach (var port in result.Entries)
            {
                _ports.Add(port);
            }

            LastScannedAt = result.ScannedAt;
            IsElevated = result.IsElevated;
            PermissionNotice = result.PermissionNotice ?? PermissionNotice;
            SelectedPort = _ports.FirstOrDefault();
            PortsView.Refresh();
            RaiseCountProperties();

            StatusMessage = result.Warning is null
                ? $"扫描完成，发现 {_ports.Count} 个端口占用。{PermissionNotice}"
                : $"扫描失败：{result.Warning}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "扫描已取消";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task KillSelectedProcessAsync()
    {
        if (SelectedPort is null)
        {
            return;
        }

        var selected = SelectedPort;
        var serviceText = selected.IsSvchost && selected.Services.Count > 0
            ? $"{Environment.NewLine}{Environment.NewLine}该 svchost 当前承载服务：{string.Join(", ", selected.Services.Select(service => service.Name))}"
            : string.Empty;

        var result = MessageBox.Show(
            $"确定要结束进程 {selected.ProcessName} (PID {selected.ProcessId}) 吗？{serviceText}",
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
        catch (Exception exception)
        {
            StatusMessage = $"结束进程失败：{exception.Message}";
            MessageBox.Show(StatusMessage, "操作失败", MessageBoxButton.OK, MessageBoxImage.Error);
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

        var keyword = SearchText.Trim();
        return Contains(port.ProtocolText, keyword)
            || Contains(port.LocalEndpoint, keyword)
            || Contains(port.RemoteEndpoint, keyword)
            || Contains(port.State, keyword)
            || Contains(port.ProcessId.ToString(), keyword)
            || Contains(port.ProcessName, keyword)
            || Contains(port.ProcessPath, keyword)
            || Contains(port.CommandLine, keyword)
            || Contains(port.UserName, keyword)
            || port.Services.Any(service => Contains(service.Name, keyword) || Contains(service.DisplayName, keyword));
    }

    private static bool Contains(string? source, string keyword)
    {
        return source?.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void RaiseCountProperties()
    {
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(TcpCount));
        OnPropertyChanged(nameof(UdpCount));
        OnPropertyChanged(nameof(SvchostCount));
        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(SelectedServicesCount));
    }
}
