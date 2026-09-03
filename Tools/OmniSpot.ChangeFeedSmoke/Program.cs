using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using SmartFileLauncher.Core.ChangeFeed.Ipc;

namespace OmniSpot.ChangeFeedSmoke;

[SupportedOSPlatform("windows")]
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitKullanim = 64;
    private const int ExitHata = 2;
    private const int ExitReddedildi = 3;

    private static async Task<int> Main(string[] args)
    {
        var command = (args.FirstOrDefault() ?? "yardim").ToLowerInvariant();
        var argument = args.Skip(1).FirstOrDefault();

        try
        {
            return command switch
            {
                "kimlik" => Kimlik(),
                "sahip" => Sahip(),
                "listele" => await Gonder(
                    new ChangeFeedRequest(
                        ChangeFeedProtocol.Version,
                        ChangeFeedRequestKind.ListRoots)),
                "ekle" => argument is null
                    ? Kullanim()
                    : await Gonder(
                        new ChangeFeedRequest(
                            ChangeFeedProtocol.Version,
                            ChangeFeedRequestKind.AddRoot,
                            argument)),
                "kaldir" => argument is null
                    ? Kullanim()
                    : await Gonder(
                        new ChangeFeedRequest(
                            ChangeFeedProtocol.Version,
                            ChangeFeedRequestKind.RemoveRoot,
                            argument)),
                _ => Kullanim()
            };
        }
        catch (Exception failure)
        {
            Console.WriteLine($"hata        {failure.GetType().Name}: {failure.Message}");
            return ExitHata;
        }
    }

    private static int Kimlik()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);

        Console.WriteLine($"kullanici   {identity.Name}");
        Console.WriteLine($"sid         {identity.User?.Value ?? "(okunamadi)"}");
        Console.WriteLine($"yonetici    {Evet(principal.IsInRole(WindowsBuiltInRole.Administrator))}");
        Console.WriteLine($"localsystem {Evet(IsLocalSystem(identity.User))}");

        return ExitOk;
    }

    private static int Sahip()
    {
        using var pipe = ChangeFeedPipeFactory.Connect(
            ChangeFeedProtocol.PipeName,
            TokenImpersonationLevel.Impersonation);

        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var trusted = IsLocalSystem(owner);

        Console.WriteLine($"kanal       {ChangeFeedProtocol.PipeName}");
        Console.WriteLine($"sahip       {owner?.Value ?? "(okunamadi)"}");
        Console.WriteLine($"cozum       {Cozumle(owner)}");
        Console.WriteLine($"localsystem {Evet(trusted)}");

        return trusted ? ExitOk : ExitReddedildi;
    }

    private static async Task<int> Gonder(ChangeFeedRequest request)
    {
        var client = new ChangeFeedClient();
        var response = await client.SendAsync(request, CancellationToken.None);

        Console.WriteLine($"istek       {request.Kind}");
        Console.WriteLine($"durum       {response.Status}");

        if (!string.IsNullOrWhiteSpace(response.Message))
        {
            Console.WriteLine($"mesaj       {response.Message}");
        }

        if (response.Roots is not null)
        {
            foreach (var root in response.Roots)
            {
                Console.WriteLine($"kok         {root}");
            }
        }

        return response.Status == ChangeFeedResponseStatus.Ok ? ExitOk : ExitReddedildi;
    }

    private static int Kullanim()
    {
        Console.WriteLine("OmniSpot degisiklik akisi kabul istemcisi");
        Console.WriteLine();
        Console.WriteLine("  kimlik           calisan hesabi ve SID'i yazar");
        Console.WriteLine("  sahip            kanalin sahibini okur, LocalSystem mi soyler");
        Console.WriteLine("  listele          onayli kokleri listeler");
        Console.WriteLine("  ekle <yol>       kok onayi ister");
        Console.WriteLine("  kaldir <yol>     kok onayini geri alir");
        Console.WriteLine();
        Console.WriteLine("cikis kodlari: 0 basarili, 3 reddedildi, 2 hata, 64 kullanim");

        return ExitKullanim;
    }

    private static bool IsLocalSystem(SecurityIdentifier? sid) =>
        sid is not null && sid.IsWellKnown(WellKnownSidType.LocalSystemSid);

    private static string Cozumle(SecurityIdentifier? sid)
    {
        if (sid is null)
        {
            return "(yok)";
        }

        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return "(eslenmedi)";
        }
    }

    private static string Evet(bool value) => value ? "evet" : "hayir";
}
