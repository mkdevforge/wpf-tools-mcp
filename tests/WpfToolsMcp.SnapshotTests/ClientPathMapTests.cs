using WpfToolsMcp.Automation;

namespace WpfToolsMcp.SnapshotTests;

[TestFixture]
public sealed class ClientPathMapTests
{
    private static readonly ClientPathMap Map = new(
    [
        new PathMapping(@"\\vmware-host\Shared Folders\wpf-qa", @"D:\wpf-qa\shared"),
        new PathMapping(@"\\vmware-host\Shared Folders\wpf-qa\builds", @"E:\builds"),
        new PathMapping(@"Z:\", @"D:\drive-z")
    ]);

    [TestCase(@"\\vmware-host\Shared Folders\wpf-qa\shots\a.png", @"D:\wpf-qa\shared\shots\a.png")]
    [TestCase(@"\\VMWARE-HOST\shared folders\WPF-QA\a.png", @"D:\wpf-qa\shared\a.png")]
    [TestCase(@"\\vmware-host\Shared Folders\wpf-qa", @"D:\wpf-qa\shared")]
    [TestCase(@"\\vmware-host\Shared Folders\wpf-qa\", @"D:\wpf-qa\shared")]
    [TestCase(@"//vmware-host/Shared Folders/wpf-qa/a.png", @"D:\wpf-qa\shared\a.png")]
    [TestCase(@"\\vmware-host\Shared Folders\wpf-qa\builds\App.exe", @"E:\builds\App.exe")]
    [TestCase(@"Z:\a\b.txt", @"D:\drive-z\a\b.txt")]
    public void ToClient_maps_paths_under_a_server_folder(string serverPath, string expected) =>
        Assert.That(Map.ToClient(serverPath), Is.EqualTo(expected));

    [TestCase(@"\\vmware-host\Shared Folders\wpf-qa-other\a.png")]
    [TestCase(@"C:\Users\qa\AppData\Local\Temp\a.png")]
    [TestCase(@"relative\a.png")]
    [TestCase("")]
    public void ToClient_keeps_paths_outside_the_mapped_folders(string serverPath) =>
        Assert.That(Map.ToClient(serverPath), Is.EqualTo(serverPath));

    [TestCase(@"D:\wpf-qa\shared\app\App.exe", @"\\vmware-host\Shared Folders\wpf-qa\app\App.exe")]
    [TestCase(@"E:\builds", @"\\vmware-host\Shared Folders\wpf-qa\builds")]
    [TestCase(@"D:\drive-z", @"Z:\")]
    [TestCase(@"D:\wpf-qa\shared-not\App.exe", @"D:\wpf-qa\shared-not\App.exe")]
    public void ToServer_maps_client_paths_back(string clientPath, string expected) =>
        Assert.That(Map.ToServer(clientPath), Is.EqualTo(expected));

    // Hyper-V VMs and remote machines share a local folder over SMB; the client
    // sees it under a UNC path that names the machine.
    private static readonly ClientPathMap SmbMap = new(
    [
        new PathMapping(@"C:\WpfQa\shared", @"\\qa-vm.mshome.net\wpf-qa")
    ]);

    [TestCase(@"C:\WpfQa\shared\artifacts\screenshots\a.png", @"\\qa-vm.mshome.net\wpf-qa\artifacts\screenshots\a.png")]
    [TestCase(@"c:\wpfqa\SHARED\artifacts", @"\\qa-vm.mshome.net\wpf-qa\artifacts")]
    [TestCase(@"C:\WpfQa\shared", @"\\qa-vm.mshome.net\wpf-qa")]
    [TestCase(@"C:\WpfQa\shared-old\a.png", @"C:\WpfQa\shared-old\a.png")]
    public void ToClient_maps_a_local_server_folder_to_a_unc_client_path(string serverPath, string expected) =>
        Assert.That(SmbMap.ToClient(serverPath), Is.EqualTo(expected));

    [TestCase(@"\\qa-vm.mshome.net\wpf-qa\apps\App\App.exe", @"C:\WpfQa\shared\apps\App\App.exe")]
    [TestCase(@"\\QA-VM.MSHOME.NET\WPF-QA\apps\App.exe", @"C:\WpfQa\shared\apps\App.exe")]
    [TestCase(@"//qa-vm.mshome.net/wpf-qa/apps/App.exe", @"C:\WpfQa\shared\apps\App.exe")]
    [TestCase(@"\\qa-vm.mshome.net\wpf-qa-other\App.exe", @"\\qa-vm.mshome.net\wpf-qa-other\App.exe")]
    [TestCase(@"\\other-vm\wpf-qa\App.exe", @"\\other-vm\wpf-qa\App.exe")]
    public void ToServer_maps_a_unc_client_path_to_the_local_server_folder(string clientPath, string expected) =>
        Assert.That(SmbMap.ToServer(clientPath), Is.EqualTo(expected));

    [Test]
    public void An_empty_map_changes_nothing()
    {
        var map = new ClientPathMap([]);
        Assert.Multiple(() =>
        {
            Assert.That(map.ToClient(@"C:\a\b.png"), Is.EqualTo(@"C:\a\b.png"));
            Assert.That(map.ToServer(@"C:\a\b.png"), Is.EqualTo(@"C:\a\b.png"));
            Assert.That(map.ToServerOrNull(null), Is.Null);
            Assert.That(map.ToClientOrNull(null), Is.Null);
        });
    }

    [Test]
    public void ParseMapping_splits_server_and_client_paths()
    {
        var mapping = ClientPathMap.ParseMapping(@"\\vmware-host\Shared Folders\wpf-qa = D:\wpf-qa\shared");
        Assert.That(mapping, Is.EqualTo(new PathMapping(@"\\vmware-host\Shared Folders\wpf-qa", @"D:\wpf-qa\shared")));
    }

    [TestCase(@"C:\only-one-side")]
    [TestCase(@"C:\a=D:\b=c")]
    [TestCase(@"=D:\b")]
    public void ParseMapping_rejects_malformed_values(string value) =>
        Assert.Throws<ArgumentException>(() => ClientPathMap.ParseMapping(value));

    [TestCase(@"relative", @"D:\client")]
    [TestCase(@"C:\server", @"client")]
    [TestCase(@"\\", @"D:\client")]
    public void Mappings_require_absolute_folders(string serverPath, string clientPath) =>
        Assert.Throws<ArgumentException>(() => _ = new ClientPathMap([new PathMapping(serverPath, clientPath)]));
}
