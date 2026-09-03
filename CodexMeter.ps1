Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.Drawing

function Resolve-CodexExecutable {
  $pathCommand = Get-Command -Name 'codex.exe' -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if ($pathCommand -and (Test-Path -LiteralPath $pathCommand.Source -PathType Leaf)) {
    return $pathCommand.Source
  }

  $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
  if ([string]::IsNullOrWhiteSpace($localAppData)) {
    $localAppData = $env:LOCALAPPDATA
  }

  if (-not [string]::IsNullOrWhiteSpace($localAppData)) {
    $desktopBinRoot = Join-Path $localAppData 'OpenAI\Codex\bin'
    if (Test-Path -LiteralPath $desktopBinRoot -PathType Container) {
      $desktopExecutable = Get-ChildItem -LiteralPath $desktopBinRoot -Filter 'codex.exe' -File -Recurse -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
      if ($desktopExecutable) {
        return $desktopExecutable.FullName
      }
    }
  }

  throw [IO.FileNotFoundException]::new(
    '未找到 Codex 命令行组件。请确认 Codex 桌面应用已安装并至少启动过一次。'
  )
}

$script:instanceMutexName = 'Local\CodexMeter.SingleInstance.v1'
$script:activationEventName = 'Local\CodexMeter.Activate.v1'
$createdNew = $false
$script:instanceMutex = [Threading.Mutex]::new($true, $script:instanceMutexName, [ref]$createdNew)

if (-not $createdNew) {
  for ($attempt = 0; $attempt -lt 10; $attempt++) {
    try {
      $activationEvent = [Threading.EventWaitHandle]::OpenExisting($script:activationEventName)
      $activationEvent.Set()
      $activationEvent.Dispose()
      break
    }
    catch [Threading.WaitHandleCannotBeOpenedException] {
      Start-Sleep -Milliseconds 100
    }
    catch [UnauthorizedAccessException] {
      break
    }
  }
  $script:instanceMutex.Dispose()
  return
}

$createdEvent = $false
$script:activationSignal = [Threading.EventWaitHandle]::new(
  $false,
  [Threading.EventResetMode]::AutoReset,
  $script:activationEventName,
  [ref]$createdEvent
)

$application = [Windows.Application]::new()
$application.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown

$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        Title="Codex Meter" Width="332" SizeToContent="Height" MinHeight="180"
        WindowStyle="None" ResizeMode="NoResize" AllowsTransparency="True"
        Background="Transparent" ShowInTaskbar="False" Topmost="True">
  <Border Name="Card" Background="#F2181A1F" BorderBrush="#3CFFFFFF" BorderThickness="1"
          CornerRadius="18" Padding="18">
    <Border.Effect><DropShadowEffect Color="#000000" BlurRadius="28" ShadowDepth="8" Opacity="0.42" /></Border.Effect>
    <Grid>
      <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
      <Grid Grid.Row="0" Margin="0,0,0,14">
        <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
        <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
          <Border Width="10" Height="10" CornerRadius="5" Background="#51D88A" Margin="0,0,9,0"/>
          <TextBlock Text="CODEX METER" Foreground="#F4F4F5" FontFamily="Segoe UI Semibold" FontSize="13"/>
        </StackPanel>
        <Button Name="RefreshButton" Grid.Column="1" Content="刷新" ToolTip="立即刷新" MinWidth="48" Height="34" Margin="0,0,6,0"
                FontSize="12" Foreground="#D4D4D8" Background="#263F3F46" BorderBrush="#4D71717A"
                BorderThickness="1" Cursor="Hand" Padding="10,0"/>
        <Button Name="HideButton" Grid.Column="2" Content="隐藏" ToolTip="隐藏到系统托盘" MinWidth="48" Height="34"
                FontSize="12" Foreground="#D4D4D8" Background="#263F3F46" BorderBrush="#4D71717A"
                BorderThickness="1" Cursor="Hand" Padding="10,0"/>
      </Grid>
      <StackPanel Name="UsagePanel" Grid.Row="1"/>
      <Grid Grid.Row="2" Margin="0,5,0,0">
        <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
        <TextBlock Name="StatusText" Text="正在读取额度…" Foreground="#A1A1AA" FontSize="12"/>
        <TextBlock Name="PlanText" Grid.Column="1" Foreground="#A1A1AA" FontSize="12"/>
      </Grid>
    </Grid>
  </Border>
</Window>
'@

$reader = [System.Xml.XmlNodeReader]::new([xml]$xaml)
$window = [Windows.Markup.XamlReader]::Load($reader)
$card = $window.FindName('Card')
$refreshButton = $window.FindName('RefreshButton')
$hideButton = $window.FindName('HideButton')
$usagePanel = $window.FindName('UsagePanel')
$statusText = $window.FindName('StatusText')
$planText = $window.FindName('PlanText')
$script:refreshing = $false
$script:reallyClose = $false
$script:initialPlacementComplete = $false

function Show-MeterWindow {
  if ($window.WindowState -eq [Windows.WindowState]::Minimized) {
    $window.WindowState = [Windows.WindowState]::Normal
  }
  if (-not $window.IsVisible) {
    $window.Show()
  }

  if ($script:initialPlacementComplete) {
    Keep-MeterInsideWorkArea
  }

  $window.Topmost = $false
  $window.Topmost = $true
  $window.Activate()
  $window.Focus()
  $window.Opacity = 1.0
  $tray.Visible = $true
}

function Hide-MeterWindow {
  $window.Hide()
  $tray.Visible = $true
}

function Place-MeterAtBottomRight {
  $window.UpdateLayout()
  $area = [Windows.SystemParameters]::WorkArea
  $margin = 20.0
  $window.Left = [Math]::Max($area.Left + $margin, $area.Right - $window.ActualWidth - $margin)
  $window.Top = [Math]::Max($area.Top + $margin, $area.Bottom - $window.ActualHeight - $margin)
}

function Keep-MeterInsideWorkArea {
  $window.UpdateLayout()
  $area = [Windows.SystemParameters]::WorkArea
  $margin = 20.0
  $minLeft = $area.Left + $margin
  $minTop = $area.Top + $margin
  $maxLeft = [Math]::Max($minLeft, $area.Right - $window.ActualWidth - $margin)
  $maxTop = [Math]::Max($minTop, $area.Bottom - $window.ActualHeight - $margin)

  $window.Left = [Math]::Min([Math]::Max($window.Left, $minLeft), $maxLeft)
  $window.Top = [Math]::Min([Math]::Max($window.Top, $minTop), $maxTop)
}

function New-Brush([string]$color) {
  return [Windows.Media.BrushConverter]::new().ConvertFromString($color)
}

function Get-RemainingBrush([int]$percent) {
  if ($percent -le 15) { return New-Brush '#FB7185' }
  if ($percent -le 35) { return New-Brush '#FBBF24' }
  return New-Brush '#51D88A'
}

function Get-WindowLabel($minutes) {
  if ($null -eq $minutes) { return '当前额度' }
  if ($minutes -eq 300) { return '5 小时额度' }
  if ($minutes -eq 10080) { return '每周额度' }
  if ($minutes % 1440 -eq 0) { return "$(($minutes / 1440)) 天额度" }
  if ($minutes % 60 -eq 0) { return "$(($minutes / 60)) 小时额度" }
  return "$minutes 分钟额度"
}

function Read-RpcResponse($process, [int]$expectedId) {
  while (-not $process.HasExited) {
    $line = $process.StandardOutput.ReadLine()
    if ($null -eq $line) { break }
    $message = $line | ConvertFrom-Json
    if ($message.id -eq $expectedId) { return $message }
  }
  $details = $process.StandardError.ReadToEnd().Trim()
  if ($details) { throw $details }
  throw 'Codex 本地服务意外退出。'
}

function Send-Rpc($process, $message) {
  $process.StandardInput.WriteLine(($message | ConvertTo-Json -Compress -Depth 8))
  $process.StandardInput.Flush()
}

function Get-CodexUsage {
  $info = [Diagnostics.ProcessStartInfo]::new()
  $info.FileName = Resolve-CodexExecutable
  $info.Arguments = 'app-server --stdio'
  $info.UseShellExecute = $false
  $info.CreateNoWindow = $true
  $info.RedirectStandardInput = $true
  $info.RedirectStandardOutput = $true
  $info.RedirectStandardError = $true
  $process = [Diagnostics.Process]::Start($info)
  if ($null -eq $process) { throw '无法启动 Codex。请先安装并登录 Codex 桌面应用。' }

  try {
    Send-Rpc $process @{ id = 1; method = 'initialize'; params = @{ clientInfo = @{ name = 'codex-meter'; version = '1.0.0' }; capabilities = @{} } }
    $null = Read-RpcResponse $process 1
    Send-Rpc $process @{ method = 'initialized' }
    Send-Rpc $process @{ id = 2; method = 'account/rateLimits/read' }
    $response = Read-RpcResponse $process 2
    if ($response.error) { throw $response.error.message }
    if (-not $response.result) { throw 'Codex 没有返回额度数据。' }
    return $response.result
  }
  finally {
    if (-not $process.HasExited) { $process.Kill() }
    $process.Dispose()
  }
}

function Add-UsageWindow($parent, $data) {
  $remaining = [Math]::Max(0, [Math]::Min(100, 100 - [int]$data.usedPercent))
  $grid = [Windows.Controls.Grid]::new()
  $grid.Margin = '0,0,0,9'
  $grid.RowDefinitions.Add([Windows.Controls.RowDefinition]::new())
  $grid.RowDefinitions.Add([Windows.Controls.RowDefinition]::new())
  $grid.ColumnDefinitions.Add([Windows.Controls.ColumnDefinition]::new())
  $autoColumn = [Windows.Controls.ColumnDefinition]::new(); $autoColumn.Width = 'Auto'; $grid.ColumnDefinitions.Add($autoColumn)

  $label = [Windows.Controls.TextBlock]::new()
  $label.Text = Get-WindowLabel $data.windowDurationMins
  $label.Foreground = New-Brush '#A1A1AA'; $label.FontSize = 12
  $value = [Windows.Controls.TextBlock]::new()
  $value.Text = "剩余 $remaining%"; $value.Foreground = Get-RemainingBrush $remaining
  $value.FontSize = 12; $value.FontWeight = 'SemiBold'
  [Windows.Controls.Grid]::SetColumn($value, 1)

  $track = [Windows.Controls.Border]::new()
  $track.Height = 7; $track.CornerRadius = 4; $track.Background = New-Brush '#3F3F46'; $track.Margin = '0,6,0,0'; $track.ClipToBounds = $true
  $fill = [Windows.Controls.Border]::new()
  $fill.HorizontalAlignment = 'Left'; $fill.Background = Get-RemainingBrush $remaining; $fill.CornerRadius = 4
  $track.Child = $fill
  $track.Tag = @{ Fill = $fill; Remaining = $remaining }
  $track.Add_SizeChanged({ param($sender, $eventArgs) $sender.Tag.Fill.Width = $eventArgs.NewSize.Width * $sender.Tag.Remaining / 100 })
  [Windows.Controls.Grid]::SetRow($track, 1); [Windows.Controls.Grid]::SetColumnSpan($track, 2)
  if ($data.resetsAt) {
    $reset = [DateTimeOffset]::FromUnixTimeSeconds([long]$data.resetsAt).LocalDateTime
    $grid.ToolTip = $reset.ToString('yyyy-MM-dd HH:mm') + ' 重置'
  }
  $grid.Children.Add($label) | Out-Null; $grid.Children.Add($value) | Out-Null; $grid.Children.Add($track) | Out-Null
  $parent.Children.Add($grid) | Out-Null
}

function Update-Display($result) {
  $usagePanel.Children.Clear()
  $items = @()
  if ($result.rateLimitsByLimitId) {
    foreach ($property in $result.rateLimitsByLimitId.PSObject.Properties) { $items += $property.Value }
  } elseif ($result.rateLimits) { $items += $result.rateLimits }
  $items = $items | Sort-Object @{ Expression = { if ($_.limitId -eq 'codex') { 0 } else { 1 } } }, limitName
  if ($items.Count -eq 0) { throw '当前账户没有可显示的 Codex 额度池。' }

  foreach ($item in $items) {
    $section = [Windows.Controls.StackPanel]::new(); $section.Margin = '0,0,0,9'
    $title = [Windows.Controls.TextBlock]::new()
    $title.Text = if ($item.limitName) { $item.limitName } elseif ($item.limitId -eq 'codex') { 'Codex' } else { $item.limitId }
    $title.Foreground = New-Brush '#FAFAFA'; $title.FontSize = 14; $title.FontWeight = 'SemiBold'; $title.Margin = '0,0,0,9'
    $section.Children.Add($title) | Out-Null
    if ($item.primary) { Add-UsageWindow $section $item.primary }
    if ($item.secondary) { Add-UsageWindow $section $item.secondary }
    $usagePanel.Children.Add($section) | Out-Null
  }
  $plan = ($items | Where-Object planType | Select-Object -First 1).planType
  $planText.Text = if ($plan) { $plan.ToString().ToUpperInvariant() } else { '' }
  $primaryRemaining = 100 - [int]$items[0].primary.usedPercent
  $tray.Text = "Codex Meter · $primaryRemaining% 剩余"
}

function Refresh-Usage {
  if ($script:refreshing) { return }
  $script:refreshing = $true; $refreshButton.IsEnabled = $false; $statusText.Text = '正在刷新…'
  try {
    Update-Display (Get-CodexUsage)
    $statusText.Text = '更新于 ' + (Get-Date -Format 'HH:mm')
  } catch {
    $usagePanel.Children.Clear()
    $errorText = [Windows.Controls.TextBlock]::new()
    $errorText.Text = $_.Exception.Message; $errorText.Foreground = New-Brush '#FDA4AF'; $errorText.FontSize = 13
    $errorText.TextWrapping = 'Wrap'; $errorText.LineHeight = 20; $errorText.Margin = '0,4,0,6'
    $usagePanel.Children.Add($errorText) | Out-Null
    $statusText.Text = '读取失败 · 1 分钟后重试'; $planText.Text = ''
  } finally { $refreshButton.IsEnabled = $true; $script:refreshing = $false }

  if ($script:initialPlacementComplete) {
    Keep-MeterInsideWorkArea
  }
}

$tray = [Windows.Forms.NotifyIcon]::new()
$tray.Icon = [Drawing.SystemIcons]::Application; $tray.Text = 'Codex Meter'; $tray.Visible = $true
$menu = [Windows.Forms.ContextMenuStrip]::new()
$showItem = $menu.Items.Add('显示 / 隐藏')
$refreshItem = $menu.Items.Add('立即刷新')
$null = $menu.Items.Add('-')
$exitItem = $menu.Items.Add('退出')
$tray.ContextMenuStrip = $menu

$toggle = {
  if ($window.IsVisible -and $window.WindowState -ne [Windows.WindowState]::Minimized) {
    Hide-MeterWindow
  } else {
    Show-MeterWindow
  }
}
$showItem.Add_Click($toggle); $tray.Add_DoubleClick($toggle)
$refreshItem.Add_Click({ Refresh-Usage }); $refreshButton.Add_Click({ Refresh-Usage })
$hideButton.Add_Click({ Hide-MeterWindow })
$exitItem.Add_Click({
  $script:reallyClose = $true
  $application.Shutdown()
})
$card.Add_MouseLeftButtonDown({ param($sender, $eventArgs) if ($eventArgs.LeftButton -eq 'Pressed' -and $eventArgs.OriginalSource -isnot [Windows.Controls.Button]) { $window.DragMove() } })
$window.Add_Activated({ $window.Opacity = 1.0 })
$window.Add_Deactivated({ $window.Opacity = 0.30 })
$window.Add_Closing({
  param($sender, $eventArgs)
  if (-not $script:reallyClose) {
    $eventArgs.Cancel = $true
    Hide-MeterWindow
  }
})
$window.Add_Closed({
  $activationTimer.Stop()
  $tray.Dispose()
  if ($script:activationSignal) {
    $script:activationSignal.Dispose()
    $script:activationSignal = $null
  }
  if ($script:instanceMutex) {
    try { $script:instanceMutex.ReleaseMutex() } catch [System.ApplicationException] { }
    $script:instanceMutex.Dispose()
    $script:instanceMutex = $null
  }
})

$timer = [Windows.Threading.DispatcherTimer]::new()
$timer.Interval = [TimeSpan]::FromMinutes(1); $timer.Add_Tick({ Refresh-Usage }); $timer.Start()
$activationTimer = [Windows.Threading.DispatcherTimer]::new()
$activationTimer.Interval = [TimeSpan]::FromMilliseconds(250)
$activationTimer.Add_Tick({
  if ($script:activationSignal -and $script:activationSignal.WaitOne(0)) {
    Show-MeterWindow
  }
})
$activationTimer.Start()
$window.Add_Loaded({
  Refresh-Usage
  Place-MeterAtBottomRight
  $script:initialPlacementComplete = $true
})
$null = $application.Run($window)
