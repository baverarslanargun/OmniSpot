using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class FileDragFeedbackTests
{
    [Fact]
    public async Task FeedbackTracksNegotiatedEffectInAClickThroughWindow()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var owner = new Window { Width = 250, Height = 120, ShowActivated = false };
            FileDragFeedback? feedback = null;
            try
            {
                owner.Show();
                feedback = new FileDragFeedback(owner);
                var popup = (Popup)typeof(FileDragFeedback)
                    .GetField("_popup", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(feedback)!;

                feedback.Show(DragDropEffects.Copy, @"C:\Belgeler");
                Assert.True(popup.IsOpen);
                var content = (StackPanel)((Border)popup.Child).Child;
                var operation = (TextBlock)content.Children[0];
                Assert.Equal("+ Kopyala → Belgeler", operation.Text);

                var source = Assert.IsType<HwndSource>(PresentationSource.FromVisual(popup.Child));
                Assert.NotEqual(new WindowInteropHelper(owner).Handle, source.Handle);
                var style = GetWindowLongPtr(source.Handle, -20).ToInt64();
                Assert.NotEqual(0, style & 0x20);
                Assert.NotEqual(0, style & 0x08000000);
                Assert.False(popup.IsHitTestVisible);

                feedback.Show(DragDropEffects.Move | DragDropEffects.Scroll);
                Assert.Equal("→ Taşı (kes)", operation.Text);
                feedback.Show(DragDropEffects.None);
                Assert.Equal("Buraya bırakılamaz", operation.Text);
                feedback.Hide();
                Assert.False(popup.IsOpen);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
            finally
            {
                feedback?.Hide();
                owner.Close();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
}
