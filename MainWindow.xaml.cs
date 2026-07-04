using System;
using CanvasClient.Networking;
using CanvasClient.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CanvasClient;

public sealed partial class MainWindow : Window
{
    private ServerConnection? _connection;

    public MainWindow()
    {
        this.InitializeComponent();
        BoardControl.SyncStateChanged += (_, synced) =>
        {
            if (synced) StatusText.Text = "Đã đồng bộ — sẵn sàng vẽ!";
        };
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out int port))
        {
            StatusText.Text = "Port không hợp lệ.";
            return;
        }

        ConnectButton.IsEnabled = false;
        try
        {
            if (_connection is not null)
                await _connection.DisposeAsync();

            var connection = new ServerConnection();
            connection.Disconnected   += OnDisconnected;
            connection.PacketReceived += OnPacketReceived;

            StatusText.Text = "Đang kết nối...";
            await connection.ConnectAsync(HostBox.Text.Trim(), port);
            _connection = connection;

            BoardControl.ResetForNewRoom();
            BoardControl.AttachConnection(connection);
            BoardControl.StrokeColor     = ColorPickerControl.Color;
            BoardControl.StrokeThickness = ThicknessSlider.Value;

            StatusText.Text = "Đã kết nối — đang tạo phòng...";
            connection.Send(new CreateRoomRequestPacket(RoomBox.Text.Trim(), NameBox.Text.Trim(), null));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Lỗi kết nối: {ex.Message}";
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    // Chạy trên thread nền (thread đọc socket) — chỉ được đụng UI qua DispatcherQueue.
    private void OnPacketReceived(IPacket packet)
    {
        switch (packet)
        {
            case RoomStateInfoPacket info:
                DispatcherQueue.TryEnqueue(() =>
                {
                    BoardControl.LocalSessionId = info.YourSessionId;
                    StatusText.Text = $"Trong phòng \"{info.RoomName}\" ({info.Members.Count} thành viên) — đang đồng bộ...";
                });
                break;

            case JoinRoomRejectedPacket rejected:
                DispatcherQueue.TryEnqueue(() => StatusText.Text = $"Bị từ chối vào phòng: {rejected.Reason}");
                break;
        }
    }

    private void OnDisconnected(Exception? ex)
    {
        DispatcherQueue.TryEnqueue(() =>
            StatusText.Text = ex is null ? "Mất kết nối tới máy chủ." : $"Mất kết nối: {ex.Message}");
    }

    private void ColorPickerControl_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (BoardControl != null)
        {
            BoardControl.StrokeColor = args.NewColor;
        }
    }

    private void ThicknessSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (BoardControl != null)
        {
            BoardControl.StrokeThickness = e.NewValue;
        }
    }
}
