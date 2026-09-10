using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MachineVisionApp.Views;

public partial class IndustrialPanel : UserControl
{
    // ── Exposed Elements ─────────────────────────────────────────────

    // Modbus TCP
    public Ellipse ModbusStatusDotEl => ModbusStatusDot;
    public TextBlock ModbusStatusTextEl => ModbusStatusText;
    public TextBox ModbusIpTextBoxEl => ModbusIpTextBox;
    public TextBox ModbusPortTextBoxEl => ModbusPortTextBox;
    public TextBox ModbusUnitTextBoxEl => ModbusUnitTextBox;
    public Button ModbusConnectButtonEl => ModbusConnectButton;
    public Button ModbusDisconnectButtonEl => ModbusDisconnectButton;
    public TextBox ModbusAddrTextBoxEl => ModbusAddrTextBox;
    public TextBox ModbusValueTextBoxEl => ModbusValueTextBox;
    public Button ModbusReadButtonEl => ModbusReadButton;
    public Button ModbusWriteButtonEl => ModbusWriteButton;
    public TextBlock ModbusResultTextEl => ModbusResultText;

    // OPC-UA
    public Ellipse OpcUaStatusDotEl => OpcUaStatusDot;
    public TextBlock OpcUaStatusTextEl => OpcUaStatusText;
    public TextBox OpcUaEndpointTextBoxEl => OpcUaEndpointTextBox;
    public Button OpcUaConnectButtonEl => OpcUaConnectButton;
    public Button OpcUaDisconnectButtonEl => OpcUaDisconnectButton;
    public TextBox OpcUaNodeTextBoxEl => OpcUaNodeTextBox;
    public TextBox OpcUaValueTextBoxEl => OpcUaValueTextBox;
    public Button OpcUaReadButtonEl => OpcUaReadButton;
    public Button OpcUaWriteButtonEl => OpcUaWriteButton;
    public TextBlock OpcUaResultTextEl => OpcUaResultText;

    // Barcode Scanner
    public ComboBox ScanSourceComboBoxEl => ScanSourceComboBox;
    public Border SerialScanPanelEl => SerialScanPanel;
    public ComboBox SerialPortComboBoxEl => SerialPortComboBox;
    public ComboBox SerialBaudComboBoxEl => SerialBaudComboBox;
    public Button SerialToggleButtonEl => SerialToggleButton;
    public Border TcpScanPanelEl => TcpScanPanel;
    public TextBox TcpScanPortTextBoxEl => TcpScanPortTextBox;
    public Button TcpToggleButtonEl => TcpToggleButton;

    // Work Report
    public TextBox WorkOrderTextBoxEl => WorkOrderTextBox;
    public CheckBox CameraReportCheckBoxEl => CameraReportCheckBox;
    public TextBox PlcRegisterTextBoxEl => PlcRegisterTextBox;
    public CheckBox ModbusLinkCheckBoxEl => ModbusLinkCheckBox;
    public TextBlock TodayCountTextBlockEl => TodayCountTextBlock;
    public TextBlock LastBarcodeTextBlockEl => LastBarcodeTextBlock;
    public ListBox ReportListBoxEl => ReportListBox;
    public Button ExportCsvButtonEl => ExportCsvButton;
    public Button ClearReportButtonEl => ClearReportButton;

    // ── Events ───────────────────────────────────────────────────────

    public event EventHandler? ModbusConnectRequested;
    public event EventHandler? ModbusDisconnectRequested;
    public event EventHandler? ModbusReadRequested;
    public event EventHandler? ModbusWriteRequested;

    public event EventHandler? OpcUaConnectRequested;
    public event EventHandler? OpcUaDisconnectRequested;
    public event EventHandler? OpcUaReadRequested;
    public event EventHandler? OpcUaWriteRequested;

    public event EventHandler? SerialToggleRequested;
    public event EventHandler? TcpToggleRequested;

    public event EventHandler? ExportCsvRequested;
    public event EventHandler? ClearReportRequested;

    public event Action<int>? ScanSourceChanged;

    // ── Constructor ──────────────────────────────────────────────────

    public IndustrialPanel()
    {
        InitializeComponent();

        ModbusConnectButton.Click += (_, _) => ModbusConnectRequested?.Invoke(this, EventArgs.Empty);
        ModbusDisconnectButton.Click += (_, _) => ModbusDisconnectRequested?.Invoke(this, EventArgs.Empty);
        ModbusReadButton.Click += (_, _) => ModbusReadRequested?.Invoke(this, EventArgs.Empty);
        ModbusWriteButton.Click += (_, _) => ModbusWriteRequested?.Invoke(this, EventArgs.Empty);

        OpcUaConnectButton.Click += (_, _) => OpcUaConnectRequested?.Invoke(this, EventArgs.Empty);
        OpcUaDisconnectButton.Click += (_, _) => OpcUaDisconnectRequested?.Invoke(this, EventArgs.Empty);
        OpcUaReadButton.Click += (_, _) => OpcUaReadRequested?.Invoke(this, EventArgs.Empty);
        OpcUaWriteButton.Click += (_, _) => OpcUaWriteRequested?.Invoke(this, EventArgs.Empty);

        SerialToggleButton.Click += (_, _) => SerialToggleRequested?.Invoke(this, EventArgs.Empty);
        TcpToggleButton.Click += (_, _) => TcpToggleRequested?.Invoke(this, EventArgs.Empty);

        ExportCsvButton.Click += (_, _) => ExportCsvRequested?.Invoke(this, EventArgs.Empty);
        ClearReportButton.Click += (_, _) => ClearReportRequested?.Invoke(this, EventArgs.Empty);

        ScanSourceComboBox.SelectionChanged += ScanSourceComboBox_SelectionChanged;
    }

    // ── Public Methods ───────────────────────────────────────────────

    public void SetModbusState(bool connected, string statusText)
    {
        ModbusStatusDot.Fill = connected
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");

        ModbusStatusText.Text = statusText;
        ModbusConnectButton.IsEnabled = !connected;
        ModbusDisconnectButton.IsEnabled = connected;
    }

    public void SetOpcUaState(bool connected, string statusText)
    {
        OpcUaStatusDot.Fill = connected
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("TextMutedBrush");

        OpcUaStatusText.Text = statusText;
        OpcUaConnectButton.IsEnabled = !connected;
        OpcUaDisconnectButton.IsEnabled = connected;
    }

    public void UpdateModbusResult(string text)
    {
        ModbusResultText.Text = text;
        if (!string.IsNullOrEmpty(text))
            ModbusResultText.Foreground = (Brush)FindResource("TextSecondaryBrush");
    }

    public void UpdateOpcUaResult(string text)
    {
        OpcUaResultText.Text = text;
        if (!string.IsNullOrEmpty(text))
            OpcUaResultText.Foreground = (Brush)FindResource("TextSecondaryBrush");
    }

    public void UpdateReportStats(int todayCount, string lastBarcode)
    {
        TodayCountTextBlock.Text = todayCount.ToString();
        LastBarcodeTextBlock.Text = string.IsNullOrEmpty(lastBarcode) ? "--" : lastBarcode;
    }

    public void SetSerialPortNames(string[] ports)
    {
        SerialPortComboBox.Items.Clear();
        foreach (var port in ports)
        {
            SerialPortComboBox.Items.Add(new ComboBoxItem
            {
                Content = port,
                Style = (Style)FindResource("GlassComboBoxItem")
            });
        }
        if (SerialPortComboBox.Items.Count > 0)
            SerialPortComboBox.SelectedIndex = 0;
    }

    public void SetSerialBaudRates(string[] rates)
    {
        SerialBaudComboBox.Items.Clear();
        foreach (var rate in rates)
        {
            SerialBaudComboBox.Items.Add(new ComboBoxItem
            {
                Content = rate,
                Style = (Style)FindResource("GlassComboBoxItem")
            });
        }
        if (SerialBaudComboBox.Items.Count > 0)
            SerialBaudComboBox.SelectedIndex = 0;
    }

    // ── Event Handlers ───────────────────────────────────────────────

    private void ScanSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SerialScanPanel == null || TcpScanPanel == null) return;
        int index = ScanSourceComboBox.SelectedIndex;

        SerialScanPanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        TcpScanPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        ScanSourceChanged?.Invoke(index);
    }
}
