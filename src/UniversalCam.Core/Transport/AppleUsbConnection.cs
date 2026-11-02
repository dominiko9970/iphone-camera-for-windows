using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace UniversalCam.Core.Transport;

/// <summary>Connects to a USB-attached iPhone through Apple Mobile Device usbmux service.</summary>
internal static class AppleUsbConnection
{
    private static async Task<XDocument> Exchange(NetworkStream stream, string body, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><plist version=\"1.0\"><dict><key>ClientVersionString</key><string>UniversalCam</string><key>ProgName</key><string>UniversalCam</string>" + body + "</dict></plist>");
        var header = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length + 16);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 8);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 1);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.ReadExactlyAsync(header, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header) - 16;
        if (length < 0 || length > 1024 * 1024) throw new IOException("Invalid usbmux response");
        var reply = new byte[length];
        await stream.ReadExactlyAsync(reply, ct);
        return XDocument.Parse(Encoding.UTF8.GetString(reply));
    }

    private static XElement? Value(XElement dict, string key) =>
        dict.Elements("key").FirstOrDefault(x => x.Value == key)?.ElementsAfterSelf().FirstOrDefault();

    public static async Task<TcpClient?> TryConnectAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        var token = timeout.Token;
        using var listing = new TcpClient();
        await listing.ConnectAsync("127.0.0.1", 27015, token);
        var devices = await Exchange(listing.GetStream(), "<key>MessageType</key><string>ListDevices</string>", token);
        foreach (var device in devices.Descendants("array").Elements("dict"))
        {
            var properties = Value(device, "Properties");
            if (properties == null || Value(properties, "ConnectionType")?.Value != "USB") continue;
            if (!int.TryParse(Value(device, "DeviceID")?.Value, out int id)) continue;
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync("127.0.0.1", 27015, token);
                int port = ((7781 & 255) << 8) | (7781 >> 8);
                var result = await Exchange(client.GetStream(), $"<key>MessageType</key><string>Connect</string><key>DeviceID</key><integer>{id}</integer><key>PortNumber</key><integer>{port}</integer>", token);
                if (Value(result.Root!.Element("dict")!, "Number")?.Value == "0") return client;
            }
            catch { client.Dispose(); throw; }
            client.Dispose();
        }
        return null;
    }
}
