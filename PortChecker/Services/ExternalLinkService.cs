using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace PortChecker.Services;

internal sealed class ExternalLinkService
{
    public Task OpenUrlAsync(string url)
    {
        return Task.Run(() =>
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
            {
                throw new ArgumentException("链接地址无效。", nameof(url));
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = uri.ToString(),
                UseShellExecute = true
            });
        });
    }
}
