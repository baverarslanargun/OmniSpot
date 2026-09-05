using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

[SupportedOSPlatform("windows")]
public sealed class ChangeFeedIpcTests
{
    private static readonly SecurityIdentifier Interactive =
        new(WellKnownSidType.InteractiveSid, null);

    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);

    [Fact]
    public async Task MessageChannel_RoundTripsARequest()
    {
        using var stream = new MemoryStream();
        var sent = new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.AddRoot,
            @"C:\Projeler");

        await ChangeFeedMessageChannel.WriteRequestAsync(stream, sent, CancellationToken.None);
        stream.Position = 0;

        var received = await ChangeFeedMessageChannel.ReadRequestAsync<ChangeFeedRequest>(
            stream,
            CancellationToken.None);

        Assert.Equal(sent, received);
    }

    [Fact]
    public async Task MessageChannel_RefusesAMessageOverTheLimit()
    {
        using var stream = new MemoryStream();
        var oversized = new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.AddRoot,
            new string('k', ChangeFeedProtocol.MaximumRequestBytes));

        await Assert.ThrowsAsync<ChangeFeedProtocolException>(
            () => ChangeFeedMessageChannel.WriteRequestAsync(stream, oversized, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ChangeFeedProtocol.MaximumRequestBytes + 1)]
    public async Task MessageChannel_RefusesAnInvalidLengthPrefix(int declared)
    {
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(declared));
        stream.Position = 0;

        await Assert.ThrowsAsync<ChangeFeedProtocolException>(
            () => ChangeFeedMessageChannel.ReadRequestAsync<ChangeFeedRequest>(
                stream,
                CancellationToken.None));
    }

    [Fact]
    public async Task MessageChannel_RefusesATruncatedFrame()
    {
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(64));
        stream.Write(new byte[16]);
        stream.Position = 0;

        var failure = await Assert.ThrowsAsync<ChangeFeedProtocolException>(
            () => ChangeFeedMessageChannel.ReadRequestAsync<ChangeFeedRequest>(
                stream,
                CancellationToken.None));

        Assert.Contains("eksik geldi", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PipeSecurity_GivesCallersNoRightToCreateAnInstance()
    {
        var rules = ChangeFeedPipeFactory.BuildSecurity()
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToDictionary(rule => (SecurityIdentifier)rule.IdentityReference, rule => rule.PipeAccessRights);

        var caller = rules[Interactive];

        Assert.False(caller.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.False(caller.HasFlag(PipeAccessRights.ChangePermissions));
        Assert.False(caller.HasFlag(PipeAccessRights.TakeOwnership));
        Assert.True(caller.HasFlag(PipeAccessRights.ReadData));
        Assert.True(caller.HasFlag(PipeAccessRights.WriteData));
    }

    [Fact]
    public void PipeSecurity_GivesTheServiceAndAdministratorsFullControl()
    {
        var rules = ChangeFeedPipeFactory.BuildSecurity()
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToDictionary(rule => (SecurityIdentifier)rule.IdentityReference, rule => rule.PipeAccessRights);

        Assert.Equal(PipeAccessRights.FullControl, rules[LocalSystem]);
        Assert.Equal(
            PipeAccessRights.FullControl,
            rules[new SecurityIdentifier(ChangeFeedServiceIdentity.DeriveServiceSid())]);
        Assert.False(rules.ContainsKey(new SecurityIdentifier(WellKnownSidType.WorldSid, null)));
    }

    [Fact]
    public void PipeFactory_RefusesAClientAskingForGenericAccess()
    {
        var name = UniqueName();
        using var server = ChangeFeedPipeFactory.CreateFirstInstance(name);

        using var client = new NamedPipeClientStream(
            ".",
            name,
            PipeDirection.InOut,
            PipeOptions.None,
            TokenImpersonationLevel.Impersonation);

        Assert.Throws<UnauthorizedAccessException>(() => client.Connect(2000));
    }

    [Fact]
    public void PipeFactory_RefusesASecondInstanceOfTheSameName()
    {
        var name = UniqueName();
        using var first = ChangeFeedPipeFactory.CreateFirstInstance(name);

        var failure = Assert.Throws<ChangeFeedPipeException>(
            () => ChangeFeedPipeFactory.CreateFirstInstance(name));

        Assert.Contains("zaten var", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PipeFactory_RefusesAnUnprivilegedSecondServerInstance()
    {
        Assert.False(HoldsPrivilegedGroup());

        var name = UniqueName();
        using var first = ChangeFeedPipeFactory.CreateFirstInstance(name);

        var failure = Assert.Throws<ChangeFeedPipeException>(
            () => ChangeFeedPipeFactory.Create(name, firstInstance: false));

        Assert.Equal(5, ((Win32Exception)failure.InnerException!).NativeErrorCode);
    }

    [Fact]
    public void PipeSecurity_NamesTheCreatingProcessAsOwner()
    {
        var owner = ChangeFeedPipeFactory.BuildSecurity()
            .GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;

        Assert.Equal(ChangeFeedPipeFactory.CurrentUserSid(), owner);
    }

    [Fact]
    public void Client_TrustsOnlyALocalSystemOwnedPipeByDefault()
    {
        Assert.Equal(
            new[] { LocalSystem },
            ChangeFeedClient.DefaultTrustedOwners().ToArray());
    }

    [Fact]
    public void CallerAccessRights_CarryNothingBeyondWhatTheProtocolUses()
    {
        Assert.Equal(
            PipeAccessRights.ReadData |
            PipeAccessRights.WriteData |
            PipeAccessRights.ReadPermissions |
            PipeAccessRights.Synchronize,
            ChangeFeedPipeFactory.CallerAccessRights);

        Assert.False(ChangeFeedPipeFactory.CallerAccessRights.HasFlag(PipeAccessRights.ReadAttributes));
        Assert.False(ChangeFeedPipeFactory.CallerAccessRights.HasFlag(PipeAccessRights.WriteAttributes));
    }

    [Fact]
    public void CallerGrantRights_AddOnlyTheAttributeReadThatNpfsDemands()
    {
        Assert.Equal(
            ChangeFeedPipeFactory.CallerAccessRights | PipeAccessRights.ReadAttributes,
            ChangeFeedPipeFactory.CallerGrantRights);

        Assert.False(ChangeFeedPipeFactory.CallerGrantRights.HasFlag(PipeAccessRights.WriteAttributes));
        Assert.False(ChangeFeedPipeFactory.CallerGrantRights.HasFlag(PipeAccessRights.CreateNewInstance));
    }

    [Fact]
    public void PipeSecurity_GrantsCallersNoMoreThanTheGrantConstant()
    {
        var rules = ChangeFeedPipeFactory.BuildSecurity()
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToDictionary(rule => (SecurityIdentifier)rule.IdentityReference, rule => rule.PipeAccessRights);

        Assert.Equal(ChangeFeedPipeFactory.CallerGrantRights, rules[Interactive]);
    }

    [Fact]
    public void CallerIdentity_DistinguishesAnImpersonatedThreadFromARevertedOne()
    {
        ChangeFeedCallerIdentity.EnsureNotImpersonating();

        using var self = WindowsIdentity.GetCurrent();

        var failure = WindowsIdentity.RunImpersonated(
            self.AccessToken,
            () => Record.Exception(ChangeFeedCallerIdentity.EnsureNotImpersonating));

        Assert.IsType<ChangeFeedImpersonationException>(failure);
        Assert.Contains("bürünme token", failure!.Message, StringComparison.Ordinal);

        ChangeFeedCallerIdentity.EnsureNotImpersonating();
    }

    private static bool HoldsPrivilegedGroup()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Groups?.Contains(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)) == true;
    }

    [Fact]
    public async Task CallerIdentity_ReturnsTheConnectionSidAndReverts()
    {
        var name = UniqueName();
        using var server = ChangeFeedPipeFactory.CreateFirstInstance(name);

        var connected = server.WaitForConnectionAsync();
        using var client = Connect(name, TokenImpersonationLevel.Impersonation);
        await connected;

        var observed = ChangeFeedCallerIdentity.RunAsVerifiedCaller(server, sid => sid);

        using var self = WindowsIdentity.GetCurrent();
        Assert.Equal(self.User, observed);
        Assert.Equal(self.User, CurrentSid());
    }

    [Fact]
    public async Task CallerIdentity_RefusesAnIdentificationLevelCaller()
    {
        var name = UniqueName();
        using var server = ChangeFeedPipeFactory.CreateFirstInstance(name);

        var connected = server.WaitForConnectionAsync();
        using var client = Connect(name, TokenImpersonationLevel.Identification);
        await connected;

        Assert.Throws<ChangeFeedImpersonationException>(
            () => ChangeFeedCallerIdentity.RunAsVerifiedCaller(server, sid => sid));

        using var self = WindowsIdentity.GetCurrent();
        Assert.Equal(self.User, CurrentSid());
    }

    [Fact]
    public async Task CallerIdentity_RefusesAnAnonymousLevelCaller()
    {
        var name = UniqueName();
        using var server = ChangeFeedPipeFactory.CreateFirstInstance(name);

        var connected = server.WaitForConnectionAsync();
        using var client = Connect(name, TokenImpersonationLevel.Anonymous);
        await connected;

        Assert.Throws<ChangeFeedImpersonationException>(
            () => ChangeFeedCallerIdentity.RunAsVerifiedCaller(server, sid => sid));
    }

    [Fact]
    public void Connect_RefusesAnUnsupportedImpersonationLevel()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ChangeFeedPipeFactory.Connect(UniqueName(), TokenImpersonationLevel.None));
    }

    [Fact]
    public async Task Admission_AcceptsARootTheCallerCanList()
    {
        using var directory = new TemporaryDirectory();
        directory.CreateDirectory("alt");

        var (caller, decision) = await EvaluateAsync(directory.Path);

        using var self = WindowsIdentity.GetCurrent();
        Assert.Equal(self.User, caller);
        Assert.Equal(ChangeFeedResponseStatus.Ok, decision.Status);
        Assert.Equal(Path.TrimEndingDirectorySeparator(directory.Path), decision.CanonicalPath);
        Assert.False(decision.Identity.IsUnknown);
    }

    [Fact]
    public async Task Admission_RefusesARootThatDoesNotExist()
    {
        using var directory = new TemporaryDirectory();

        var (_, decision) = await EvaluateAsync(Path.Combine(directory.Path, "yok"));

        Assert.Equal(ChangeFeedResponseStatus.RootUnusable, decision.Status);
        Assert.Null(decision.CanonicalPath);
    }

    [Fact]
    public async Task Admission_RefusesANetworkOrRelativeRoot()
    {
        var (_, unc) = await EvaluateAsync("//sunucu/pay/Depo");
        Assert.Equal(ChangeFeedResponseStatus.RootUnusable, unc.Status);

        var (_, relative) = await EvaluateAsync(@"gorece\yol");
        Assert.Equal(ChangeFeedResponseStatus.RootUnusable, relative.Status);
    }

    [Fact]
    public async Task Admission_RefusesAnEmptyRoot()
    {
        var (_, decision) = await EvaluateAsync("   ");

        Assert.Equal(ChangeFeedResponseStatus.InvalidRequest, decision.Status);
    }

    private static async Task<(SecurityIdentifier Caller, ChangeFeedRootDecision Decision)> EvaluateAsync(
        string requestedRoot)
    {
        var name = UniqueName();
        using var server = ChangeFeedPipeFactory.CreateFirstInstance(name);

        var connected = server.WaitForConnectionAsync();
        using var client = Connect(name, TokenImpersonationLevel.Impersonation);
        await connected;

        var admission = new ChangeFeedRootAdmission(new UsnFileSystemIdentityProbe());
        return admission.Evaluate(server, requestedRoot);
    }

    private static SecurityIdentifier? CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User;
    }

    private static NamedPipeClientStream Connect(string name, TokenImpersonationLevel level)
    {
        return ChangeFeedPipeFactory.Connect(name, level);
    }

    private static string UniqueName() =>
        "OmniSpot.Test." + Guid.NewGuid().ToString("N");
}
