using System.Net;
using System.Net.Http;
using PasteJump.Core;
using PasteJump.Core.Updates;

namespace PasteJump.App.Services;

/// <summary>The outcome of asking GitHub about the latest release.</summary>
public enum UpdateCheckStatus
{
    /// <summary>A newer release exists.</summary>
    UpdateAvailable,

    /// <summary>The running version is the newest published.</summary>
    UpToDate,

    /// <summary>The repository has no published releases at all - which is not an error.</summary>
    NoReleases,

    /// <summary>The check could not be completed. <see cref="UpdateCheckResult.Detail"/> says why.</summary>
    Failed,
}

/// <param name="Status">What happened.</param>
/// <param name="Release">The release found, when there was one.</param>
/// <param name="Detail">A sentence for the user when the check failed. Empty otherwise.</param>
public readonly record struct UpdateCheckResult(
    UpdateCheckStatus Status,
    ReleaseInfo? Release,
    string Detail);

/// <summary>
/// Asks GitHub whether a newer release has been published.
/// <para>
/// Only ever when the user asks. Nothing here runs at start-up: a clipboard manager that phones home the
/// moment you sign in is doing something you did not request, and it would put a network round trip in front of
/// the tray icon appearing - the start-up cost this project has spent real effort keeping to ~300 ms.
/// </para>
/// <para>
/// It reports rather than installs. Downloading and replacing a running executable needs elevation for an
/// installed copy, and a signature to be worth trusting; neither exists yet, so the honest thing is to say a
/// version is out and open the page.
/// </para>
/// </summary>
internal static class UpdateChecker
{
    /// <summary>
    /// One client for the process. A fresh <see cref="HttpClient"/> per check is the classic way to exhaust
    /// sockets, and this one is configured once - including the User-Agent, which GitHub's API rejects requests
    /// without.
    /// </summary>
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            // Short on purpose: this is a menu item the user is waiting on, not a background sync. Failing in
            // ten seconds with "could not reach GitHub" beats a menu that appears to have hung.
            Timeout = TimeSpan.FromSeconds(10),
        };

        client.DefaultRequestHeaders.Add("User-Agent", $"PasteJump/{AppVersion.Current}");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        return client;
    }

    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var apiUrl = UpdateCheck.LatestReleaseApiUrl(AppVersion.RepositoryUrl);

        if (apiUrl is null)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                null,
                "This build does not record which repository to check.");
        }

        try
        {
            using var response = await Client.GetAsync(apiUrl, cancellationToken).ConfigureAwait(false);

            // 404 is the documented answer for a repository with no releases yet, and it is not a failure -
            // it is the state this project is in until the first one is published. Saying "could not check"
            // here would send someone hunting for a network problem that does not exist.
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(UpdateCheckStatus.NoReleases, null, string.Empty);
            }

            if (response.StatusCode == (HttpStatusCode)403 || response.StatusCode == (HttpStatusCode)429)
            {
                // GitHub allows 60 unauthenticated requests an hour per address. Reachable only by clicking
                // this repeatedly, but worth naming rather than reporting as a generic failure.
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    null,
                    "GitHub is rate-limiting this computer. Try again later.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    null,
                    $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!UpdateCheck.TryParseRelease(json, out var release))
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.NoReleases,
                    null,
                    string.Empty);
            }

            return UpdateCheck.IsNewer(AppVersion.Current, release.Tag)
                ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, release, string.Empty)
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, release, string.Empty);
        }
        catch (TaskCanceledException)
        {
            // Covers the timeout as well as cancellation - HttpClient reports its own timeout this way.
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                null,
                "GitHub did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                null,
                $"Could not reach GitHub: {ex.Message}");
        }
    }
}
