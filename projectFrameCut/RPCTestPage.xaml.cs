#if WINDOWS
using projectFrameCut.Platforms.Windows;
using projectFrameCut.Shared;
using System.Diagnostics;
using System.Text.Json;

namespace projectFrameCut;

public partial class RPCTestPage : ContentPage
{
	public RPCTestPage()
	{
		InitializeComponent();
	}

    private RpcClient? _rpc;
    private Process rpcProc;

    private async void BootRPC_Clicked(object sender, EventArgs e)
    {
        var pipeId = "pjfc_rpc_V1_" + Guid.NewGuid().ToString();
        rpcProc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "projectFrameCut.Render.WindowsRender.exe"),
                WorkingDirectory = Path.Combine(AppContext.BaseDirectory),
                Arguments = $" rpc_backend  -pipe={pipeId} -output_options=3840,2160,42,AV_PIX_FMT_NONE,nope -tempFolder={@"D:\code\playground\projectFrameCut"} ",
                UseShellExecute = true,
                CreateNoWindow = false,

            }
        };

        Debug.WriteLine($"Starting RPC backend with pipe ID: {pipeId}, args:{rpcProc.StartInfo.Arguments}");

        rpcProc.Start();
        Thread.Sleep(500);
        _rpc = new RpcClient();
        await _rpc.StartAsync(pipeId );
        await DisplayAlert("rpc started", pipeId,"ok");
        
    }

    private async void LoadDraft_Clicked(object sender, EventArgs e)
    {
        var result = await FilePicker.PickAsync(new PickOptions { PickerTitle = "选择草稿 JSON 文件" });
        if (result == null) return;
        using var stream = await result.OpenReadAsync();
        using var sr = new StreamReader(stream);
        var draftSource = await sr.ReadToEndAsync();

        await _rpc.SendAsync("UpdateClips", JsonDocument.Parse(draftSource).RootElement);
    }

    private async void GoRender_Clicked(object sender, EventArgs e)
    {
        var result = await _rpc.SendAsync("RenderOne", JsonDocument.Parse(JsonSerializer.Serialize(int.Parse(FrameToRender.Text))).RootElement);
        var path = result.Value.GetProperty("path").GetString();
        await DisplayAlert("渲染完成", path, "ok");
        RPCResultImage.Source = ImageSource.FromFile(path);
    }
}

#endif