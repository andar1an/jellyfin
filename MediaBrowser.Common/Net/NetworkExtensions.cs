using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace MediaBrowser.Common.Net
{
    /// <summary>
    /// Defines the <see cref="NetworkExtensions" />.
    /// </summary>
    public static class NetworkExtensions
    {
        /// <summary>
        /// Returns true if the IPAddress contains an IP6 Local link address.
        /// </summary>
        /// <param name="address">IPAddress object to check.</param>
        /// <returns>True if it is a local link address.</returns>
        /// <remarks>
        /// See https://stackoverflow.com/questions/6459928/explain-the-instance-properties-of-system-net-ipaddress
        /// it appears that the IPAddress.IsIPv6LinkLocal is out of date.
        /// </remarks>
        public static bool IsIPv6LinkLocal(IPAddress address)
        {
            if (address == null)
            {
                throw new ArgumentNullException(nameof(address));
            }

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (address.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return false;
            }

            // GetAddressBytes
            Span<byte> octet = stackalloc byte[16];
            address.TryWriteBytes(octet, out _);
            uint word = (uint)(octet[0] << 8) + octet[1];

            return word >= 0xfe80 && word <= 0xfebf; // fe80::/10 :Local link.
        }

        /// <summary>
        /// Convert a subnet mask in CIDR notation to a dotted decimal string value. IPv4 only.
        /// </summary>
        /// <param name="cidr">Subnet mask in CIDR notation.</param>
        /// <param name="family">IPv4 or IPv6 family.</param>
        /// <returns>String value of the subnet mask in dotted decimal notation.</returns>
        public static IPAddress CidrToMask(byte cidr, AddressFamily family)
        {
            uint addr = 0xFFFFFFFF << (family == AddressFamily.InterNetwork ? 32 : 128 - cidr);
            addr = ((addr & 0xff000000) >> 24)
                   | ((addr & 0x00ff0000) >> 8)
                   | ((addr & 0x0000ff00) << 8)
                   | ((addr & 0x000000ff) << 24);
            return new IPAddress(addr);
        }

        /// <summary>
        /// Convert a subnet mask to a CIDR. IPv4 only.
        /// https://stackoverflow.com/questions/36954345/get-cidr-from-netmask.
        /// </summary>
        /// <param name="mask">Subnet mask.</param>
        /// <returns>Byte CIDR representing the mask.</returns>
        public static byte MaskToCidr(IPAddress mask)
        {
            if (mask == null)
            {
                throw new ArgumentNullException(nameof(mask));
            }

            byte cidrnet = 0;
            if (!mask.Equals(IPAddress.Any))
            {
                // GetAddressBytes
                Span<byte> bytes = stackalloc byte[mask.AddressFamily == AddressFamily.InterNetwork ? 4 : 16];
                mask.TryWriteBytes(bytes, out _);

                var zeroed = false;
                for (var i = 0; i < bytes.Length; i++)
                {
                    for (int v = bytes[i]; (v & 0xFF) != 0; v <<= 1)
                    {
                        if (zeroed)
                        {
                            // Invalid netmask.
                            return (byte)~cidrnet;
                        }

                        if ((v & 0x80) == 0)
                        {
                            zeroed = true;
                        }
                        else
                        {
                            cidrnet++;
                        }
                    }
                }
            }

            return cidrnet;
        }

        /// <summary>
        /// Converts an IPAddress into a string.
        /// Ipv6 addresses are returned in [ ], with their scope removed.
        /// </summary>
        /// <param name="address">Address to convert.</param>
        /// <returns>URI safe conversion of the address.</returns>
        public static string FormatIpString(IPAddress? address)
        {
            if (address == null)
            {
                return string.Empty;
            }

            var str = address.ToString();
            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                int i = str.IndexOf('%', StringComparison.Ordinal);
                if (i != -1)
                {
                    str = str.Substring(0, i);
                }

                return $"[{str}]";
            }

            return str;
        }

        /// <summary>
        /// Attempts to parse a host string.
        /// </summary>
        /// <param name="host">Host name to parse.</param>
        /// <param name="addresses">Object representing the string, if it has successfully been parsed.</param>
        /// <param name="isIpv4Enabled"><c>true</c> if IPv4 is enabled.</param>
        /// <param name="isIpv6Enabled"><c>true</c> if IPv6 is enabled.</param>
        /// <returns><c>true</c> if the parsing is successful, <c>false</c> if not.</returns>
        public static bool TryParseHost(string host, [NotNullWhen(true)] out IPAddress[] addresses, bool isIpv4Enabled = true, bool isIpv6Enabled = false)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                addresses = Array.Empty<IPAddress>();
                return false;
            }

            host = host.Trim();

            // See if it's an IPv6 with port address e.g. [::1] or [::1]:120.
            if (host[0] == '[')
            {
                int i = host.IndexOf(']', StringComparison.Ordinal);
                if (i != -1)
                {
                    return TryParseHost(host.Remove(i)[1..], out addresses);
                }

                addresses = Array.Empty<IPAddress>();
                return false;
            }

            var hosts = host.Split(':');

            if (hosts.Length <= 2)
            {
                // Use regular expression as CheckHostName isn't RFC5892 compliant.
                // Modified from gSkinner's expression at https://stackoverflow.com/questions/11809631/fully-qualified-domain-name-validation
                string pattern = @"(?im)^(?!:\/\/)(?=.{1,255}$)((.{1,63}\.){0,127}(?![0-9]*$)[a-z0-9-]+\.?)(:(\d){1,5}){0,1}$";

                // Is hostname or hostname:port
                if (Regex.IsMatch(hosts[0], pattern))
                {
                    try
                    {
                        addresses = Dns.GetHostAddresses(hosts[0]);
                        return true;
                    }
                    catch (SocketException)
                    {
                        // Log and then ignore socket errors, as the result value will just be an empty array.
                        Console.WriteLine("GetHostAddresses failed.");
                    }
                }

                // Is an IP4 or IP4:port
                host = hosts[0].Split('/')[0];

                if (IPAddress.TryParse(host, out var address))
                {
                    if (((address.AddressFamily == AddressFamily.InterNetwork) && (!isIpv4Enabled && isIpv6Enabled)) ||
                        ((address.AddressFamily == AddressFamily.InterNetworkV6) && (isIpv4Enabled && !isIpv6Enabled)))
                    {
                        addresses = Array.Empty<IPAddress>();
                        return false;
                    }

                    addresses = new[] { address };

                    // Host name is an ip4 address, so fake resolve.
                    return true;
                }
            }
            else if (hosts.Length <= 9 && IPAddress.TryParse(host.Split('/')[0], out var address)) // 8 octets + port
            {
                addresses = new[] { address };
                return true;
            }

            addresses = Array.Empty<IPAddress>();
            return false;
        }
    }
}
