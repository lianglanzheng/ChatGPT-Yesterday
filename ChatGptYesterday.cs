#!/usr/bin/env -S dotnet --
#:property TargetFramework=net10.0
#:property ExperimentalFileBasedProgramEnableTransitiveDirectives=true
#:include ChatGptVersions.cs
#:include ChatGptDownload.cs

using System.Globalization;
using System.Text;

CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler onCancel = (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += onCancel;

try
{
    Console.WriteLine("Fetching installer catalog...");
    var response = await ChatGptVersions.GetCatalogAsync(cancellation.Token);
    cancellation.Token.ThrowIfCancellationRequested();
    if (response.Error is not null)
        throw new InvalidDataException($"Catalog query failed: {response.Error}");

    await ChatGptDownload.RunAsync(ChatGptVersions.ToJson(response), cancellation.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(cancellation.IsCancellationRequested ? "\nCancelled." : "\nDownload timed out.");
    return cancellation.IsCancellationRequested ? 130 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"\nError: {ex.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= onCancel;
}
