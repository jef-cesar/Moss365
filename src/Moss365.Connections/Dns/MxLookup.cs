using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Moss365.Core.Localization;
using Moss365.Core.Mail;

namespace Moss365.Connections.Dns;

/// <summary>
/// Reads the MX records of a domain with one DNS question over UDP to the DNS servers of this PC (G0, U31): no service of
/// the makers, no extra package. Only the name of the domain leaves the PC, as with every e-mail it sends.
/// </summary>
public sealed class MxLookup : IMxLookup
{
    private const ushort TypeMx = 15;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly Func<IReadOnlyList<IPAddress>> _servers;

    public MxLookup(Func<IReadOnlyList<IPAddress>>? servers = null) => _servers = servers ?? SystemServers;

    public async Task<IReadOnlyList<MxRecord>> MxAsync(string domain, CancellationToken cancel = default)
    {
        if (!MailLocations.IsDomain(domain))
        {
            throw new ArgumentException(Texts.T["mail.dns.not-a-domain"], nameof(domain));
        }

        var servers = _servers();
        if (servers.Count == 0)
        {
            throw new InvalidOperationException(Texts.T["mail.dns.no-server"]);
        }

        Exception? last = null;
        foreach (var server in servers.Take(3))
        {
            var id = (ushort)RandomNumberGenerator.GetInt32(0, ushort.MaxValue + 1);
            try
            {
                using var udp = new UdpClient(server.AddressFamily);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                timeout.CancelAfter(Timeout);
                var query = Query(domain, id);
                await udp.SendAsync(query, new IPEndPoint(server, 53), timeout.Token);
                while (true)
                {
                    var answer = await udp.ReceiveAsync(timeout.Token);
                    if (answer.Buffer.Length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(answer.Buffer) == id)
                    {
                        return Parse(answer.Buffer, id);
                    }
                }
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                last = new TimeoutException(Texts.T["mail.dns.no-answer"]);
            }
            catch (SocketException ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException(Texts.T["mail.dns.no-answer"]);
    }

    /// <summary>The DNS servers of the network adapters that are up (no loopback, no deprecated site-local IPv6).</summary>
    private static IReadOnlyList<IPAddress> SystemServers()
        => NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().DnsAddresses)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6SiteLocal))
            .Distinct()
            .ToList();

    /// <summary>A DNS question for the MX records of a domain (recursion desired).</summary>
    public static byte[] Query(string domain, ushort id)
    {
        var bytes = new List<byte>(32 + domain.Length);
        void U16(int v) { bytes.Add((byte)(v >> 8)); bytes.Add((byte)v); }
        U16(id);
        U16(0x0100); // standard query, recursion desired
        U16(1);      // one question
        U16(0);
        U16(0);
        U16(0);
        foreach (var label in domain.TrimEnd('.').Split('.'))
        {
            var b = Encoding.ASCII.GetBytes(label);
            if (b.Length is 0 or > 63)
            {
                throw new ArgumentException(Texts.T["mail.dns.not-a-domain"], nameof(domain));
            }

            bytes.Add((byte)b.Length);
            bytes.AddRange(b);
        }

        bytes.Add(0);
        U16(TypeMx);
        U16(1); // class IN
        return bytes.ToArray();
    }

    /// <summary>The MX records in a DNS answer; empty for a domain without MX or a domain that does not exist.</summary>
    public static IReadOnlyList<MxRecord> Parse(byte[] message, ushort id)
    {
        if (message.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(message) != id)
        {
            throw new InvalidDataException(Texts.T["mail.dns.bad-answer"]);
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(2));
        var rcode = flags & 0x000F;
        if (rcode == 3)
        {
            return Array.Empty<MxRecord>(); // the domain does not exist
        }

        if (rcode != 0)
        {
            throw new InvalidDataException(Texts.T.Format("mail.dns.error", rcode));
        }

        var questions = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6));
        var pos = 12;
        for (var q = 0; q < questions; q++)
        {
            ReadName(message, ref pos);
            pos += 4;
        }

        var records = new List<MxRecord>();
        for (var a = 0; a < answers && pos + 10 <= message.Length; a++)
        {
            ReadName(message, ref pos);
            var type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(pos));
            var length = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(pos + 8));
            var data = pos + 10;
            if (data + length > message.Length)
            {
                break;
            }

            if (type == TypeMx && length >= 3)
            {
                var preference = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(data));
                var namePos = data + 2;
                records.Add(new MxRecord(preference, ReadName(message, ref namePos)));
            }

            pos = data + length;
        }

        return records.OrderBy(r => r.Preference).ToList();
    }

    /// <summary>A name in a DNS message, with compression (pointers), safe against loops.</summary>
    private static string ReadName(byte[] message, ref int pos)
    {
        var labels = new List<string>();
        var jumped = false;
        var cursor = pos;
        for (var steps = 0; steps < 128; steps++)
        {
            if (cursor >= message.Length)
            {
                throw new InvalidDataException(Texts.T["mail.dns.bad-answer"]);
            }

            var length = message[cursor];
            if (length == 0)
            {
                if (!jumped)
                {
                    pos = cursor + 1;
                }

                return string.Join('.', labels);
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (cursor + 1 >= message.Length)
                {
                    throw new InvalidDataException(Texts.T["mail.dns.bad-answer"]);
                }

                var target = ((length & 0x3F) << 8) | message[cursor + 1];
                if (!jumped)
                {
                    pos = cursor + 2;
                }

                jumped = true;
                cursor = target;
                continue;
            }

            if (cursor + 1 + length > message.Length)
            {
                throw new InvalidDataException(Texts.T["mail.dns.bad-answer"]);
            }

            labels.Add(Encoding.ASCII.GetString(message, cursor + 1, length));
            cursor += 1 + length;
        }

        throw new InvalidDataException(Texts.T["mail.dns.bad-answer"]);
    }
}
