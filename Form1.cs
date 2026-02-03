using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Drawing;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using System.Data.SqlTypes;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Button;
namespace ImageScaler
{
    public partial class Form1 : Form
    {
        // --- DPI scaling (manual, baseline-at-96dpi) ---
        private const int BASELINE_DPI = 96;
        private const int WM_DPICHANGED = 0x02E0;

        private sealed class LayoutInfo
        {
            public Rectangle Bounds96;
            public AnchorStyles Anchor;
            public DockStyle Dock;

            public LayoutInfo() { }

            public LayoutInfo(Rectangle bounds96, AnchorStyles anchor, DockStyle dock)
            {
                Bounds96 = bounds96;
                Anchor = anchor;
                Dock = dock;
            }
        }

        // ---------------- DPI handling ----------------
        // The original app uses absolute positioning. With mixed-DPI monitors (e.g. 4K @ 150% + 1440p @ 100%),
        // Windows will resize the window when it crosses monitors, but WinForms won't magically reposition everything
        // unless the UI is built with DPI-aware layout. We do a simple, deterministic approach:
        //  - Treat the designer coordinates as a 96-DPI baseline
        //  - On WM_DPICHANGED (and once at startup), reapply every control's bounds as baseline * (dpi/96)
        //  - Never scale incrementally (prevents the "infinite growth" loop)

        private void CaptureBaselineLayout96()
        {
            if (_baselineCaptured) return;

            _layout96.Clear();
            _formClientSize96 = this.ClientSize;
            CaptureControlsRecursive(this);
            _baselineCaptured = true;
        }

        private void CaptureControlsRecursive(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                // Store the designer-time (unscaled) bounds as 96 DPI baseline.
                _layout96[c] = new LayoutInfo(c.Bounds, c.Anchor, c.Dock);
                if (c.Controls != null && c.Controls.Count > 0)
                    CaptureControlsRecursive(c);
            }
        }

        private static Rectangle ScaleRect(Rectangle r, float scale)
        {
            return new Rectangle(
                (int)Math.Round(r.Left * scale),
                (int)Math.Round(r.Top * scale),
                (int)Math.Round(r.Width * scale),
                (int)Math.Round(r.Height * scale));
        }

        private static Size ScaleSize(Size s, float scale)
        {
            return new Size(
                (int)Math.Round(s.Width * scale),
                (int)Math.Round(s.Height * scale));
        }

        private void ApplyDpiLayout(int newDpi, Rectangle? suggestedBounds, bool isInitial)
        {
            if (newDpi <= 0) newDpi = 96;
            CaptureBaselineLayout96();

            // Avoid re-entrancy (WM_DPICHANGED can chain through resize/layout).
            if (_applyingDpi) return;
            if (!isInitial && _lastAppliedDpi == newDpi) return;

            _applyingDpi = true;
            try
            {
                float scale = newDpi / 96f;

                // Resize/move the window FIRST, then place controls deterministically.
                if (suggestedBounds.HasValue)
                {
                    // Windows provides a recommended rectangle; using it prevents "snap back" behavior.
                    this.Bounds = suggestedBounds.Value;
                }
                else
                {
                    this.ClientSize = ScaleSize(_formClientSize96, scale);
                }

                this.SuspendLayout();
                try
                {
                    foreach (var kv in _layout96)
                    {
                        var ctrl = kv.Key;
                        if (ctrl == null || ctrl.IsDisposed) continue;

                        LayoutInfo li = kv.Value;

                        // Temporarily neutralize anchoring while we apply exact bounds.
                        AnchorStyles originalAnchor = ctrl.Anchor;
                        ctrl.Anchor = AnchorStyles.Top | AnchorStyles.Left;

                        ctrl.Bounds = ScaleRect(li.Bounds96, scale);

                        // Restore original anchor so future non-DPI layout (e.g. ApplyResponsiveLayout) still works.
                        ctrl.Anchor = li.Anchor;
                    }
                }
                finally
                {
                    this.ResumeLayout(true);
                }

                // Re-apply the bottom/status layout using the current client size.
                ApplyResponsiveLayout();

                _lastAppliedDpi = newDpi;
            }
            finally
            {
                _applyingDpi = false;
            }
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_DPICHANGED = 0x02E0;
            if (m.Msg == WM_DPICHANGED)
            {
                int dpiX = LoWord(m.WParam);

                // lParam points to a RECT with the suggested new window bounds in *screen pixels*.
                Rectangle? suggested = null;
                try
                {
                    RECT rc = Marshal.PtrToStructure<RECT>(m.LParam);
                    suggested = Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom);
                }
                catch
                {
                    // ignore
                }

                // Let WinForms do its default processing (non-DPI autoscale is disabled, but this still
                // helps Windows finish the transition cleanly), then apply our deterministic layout.
                base.WndProc(ref m);
                ApplyDpiLayout(dpiX, suggested, isInitial: false);
                return;
            }

            base.WndProc(ref m);
        }

        private readonly Dictionary<Control, LayoutInfo> _layout96 = new Dictionary<Control, LayoutInfo>();
        private Size _formClientSize96;
        private bool _baselineCaptured = false;
        private bool _applyingDpi = false;
        private int _lastAppliedDpi = 0;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private static int LoWord(IntPtr ptr) => unchecked((short)((long)ptr & 0xFFFF));
        private static int HiWord(IntPtr ptr) => unchecked((short)(((long)ptr >> 16) & 0xFFFF));

        private string selectedImagePath = string.Empty;
        private string imgscale = "2";
        private string bc="7";
        private string upstat = "";
        private string upstat2 = "";
        string dataimg = "realesr-animevideov3";
        string formatValue = null;

        // Internal diagnostic sink used for DPI troubleshooting.
        // Default is no-op; Form1_Load can rewire this to LogData.
        private Action<string> _debugLog = _ => { };

        // Job control (async + cancel + progress)
        private CancellationTokenSource _cts;
        private bool _jobRunning = false;
        private bool _suppressJobLog = false;
        private struct JobProgress
        {
            public int Percent;
            public string Status;
        }
        public Form1()
        {
            InitializeComponent();

            // Capture the designer/layout baseline *before* any responsive repositioning happens.
            // We'll treat this as our 96-DPI reference layout and deterministically scale from it.
            CaptureBaselineLayout96();

            // Apply initial DPI layout after the form is shown so Windows has finalized bounds/DPI.
            this.Shown += Form1_Shown;
            // Fix layout when resizing / DPI scaling
            this.Resize += (s, e) =>
            {
                if (!_applyingDpi)
                    ApplyResponsiveLayout();
            };
        }

        private void Form1_Shown(object sender, EventArgs e)
        {
            // Defer to the message loop so initial bounds/DPI are finalized.
            BeginInvoke(new Action(() =>
            {
                try
                {
                    int dpi = (int)GetDpiForWindow(this.Handle);
                    ApplyDpiLayout(dpi, suggestedBounds: null, isInitial: true);
                }
                catch
                {
                    // ignore
                }
            }));
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            //checkSound.Checked = Properties.Settings.Default.CheckSoundz;
            //checkTemp.Checked = Properties.Settings.Default.CheckTempz;
            //this.TopMost = CheckTop.Checked;
            //CheckTop.Checked = Properties.Settings.Default.CheckTopz;
            // Route DPI diagnostics into the existing output console.
            _debugLog = (s) => LogData(s);

            try
            {
                var initDpi = GetDpiForWindow(this.Handle);
                _debugLog($"DPI init: DeviceDpi={this.DeviceDpi}, GetDpiForWindow={initDpi}");
            }
            catch { /* ignore */ }

            LoadSettings();       
            ApplyResponsiveLayout();
        }
        private void SaveSettings()
        {
            string appPath = Application.StartupPath;
            string configPath = Path.Combine(appPath, "settings.txt");
            string[] lines = new string[]
            {
        $"CheckTopz={checkTop.Checked}",
        $"CheckSoundz={checkSound.Checked}",
        $"CheckTempz={checkTemp.Checked}"
            };
            File.WriteAllLines(configPath, lines);
        }
        private void LoadSettings()
        {
            comboBox1.SelectedIndex = 3;
            string appPath = Application.StartupPath;
            string configPath = Path.Combine(appPath, "settings.txt");
            if (File.Exists(configPath))
            {
                string[] lines = File.ReadAllLines(configPath);
                foreach (string line in lines)
                {
                    if (line.StartsWith("CheckTopz="))
                    {
                        string value = line.Substring("CheckTopz=".Length).Trim().ToLower();
                        checkTop.Checked = value == "true";
                    }
                    else if (line.StartsWith("CheckSoundz="))
                    {
                        string value = line.Substring("CheckSoundz=".Length).Trim().ToLower();
                        checkSound.Checked = value == "true";
                    }
                    else if (line.StartsWith("CheckTempz="))
                    {
                        string value = line.Substring("CheckTempz=".Length).Trim().ToLower();
                        checkTemp.Checked = value == "true";
                    }
                }
            }
            this.TopMost = checkTop.Checked;
        }
        private void ApplyResponsiveLayout()
        {
            try
            {
                int margin = 10;
                // Bottom status area
                int statusH = 16;
                int progressH = 12;
                int rowGap = 4;
                int bottomBlockH = statusH + rowGap + progressH;
                int bottomY = Math.Max(margin, this.ClientSize.Height - margin - bottomBlockH);
                int statusY = bottomY;
                int progressY = bottomY + statusH + rowGap;
                if (btnCancel != null)
                {
                    btnCancel.Location = new Point(Math.Max(margin, this.ClientSize.Width - margin - btnCancel.Width), statusY - 6);
                }
                int cancelX = (btnCancel != null) ? btnCancel.Left : (this.ClientSize.Width - margin);
                int rightLimit = cancelX - 8;
                if (progressBarJob != null)
                {
                    progressBarJob.Location = new Point(margin, progressY);
                    progressBarJob.Size = new Size(Math.Max(100, rightLimit - margin), progressH);
                }
                if (lblPercent != null)
                {
                    lblPercent.AutoSize = false;
                    lblPercent.TextAlign = ContentAlignment.MiddleRight;
                    lblPercent.Size = new Size(60, statusH);
                    lblPercent.Location = new Point(Math.Max(margin, rightLimit - lblPercent.Width), statusY);
                }
                if (lblStatus != null)
                {
                    lblStatus.AutoSize = false;
                    int percentLeft = (lblPercent != null) ? (lblPercent.Left - 8) : rightLimit;
                    lblStatus.Location = new Point(margin, statusY);
                    lblStatus.Size = new Size(Math.Max(100, percentLeft - margin), statusH);
                }
                // Logs area (txtData + txtLog)
                int logsTop = 0;
                if (panelResize != null) logsTop = Math.Max(logsTop, panelResize.Bottom);
                if (panelUpscale != null) logsTop = Math.Max(logsTop, panelUpscale.Bottom);
                if (panelBatch != null) logsTop = Math.Max(logsTop, panelBatch.Bottom);
                logsTop = Math.Max(logsTop + margin, margin);
                int logsBottom = Math.Max(logsTop + 60, statusY - margin);
                int logsHeight = Math.Max(60, logsBottom - logsTop);
                int dataW = (txtData != null) ? txtData.Width : 240;
                dataW = Math.Max(200, Math.Min(320, dataW));
                if (txtData != null)
                {
                    txtData.Location = new Point(0, logsTop);
                    txtData.Size = new Size(dataW, logsHeight);
                }
                if (txtLog != null)
                {
                    int logLeft = dataW + margin;
                    int logW = Math.Max(200, this.ClientSize.Width - logLeft - margin);
                    txtLog.Location = new Point(logLeft, logsTop);
                    txtLog.Size = new Size(logW, logsHeight);
                }
            }
            catch
            {
                // ignore layout exceptions
            }
        }
        private void PlayCompletionSound()
        {
            // Set the path to your sound file (.wav)
            string soundFilePath = "completion_sound.wav";
            // Use SoundPlayer to play the sound
            System.Media.SoundPlayer player = new System.Media.SoundPlayer(soundFilePath);
            if (checkSound.Checked == true)
            {
                player.Play();
            }
        }
        // Fungsi untuk memilih gambar
        private void BtnSelectImage_Click(object sender, EventArgs e)
        {
            txtData.Clear();
            btnResize.ForeColor = Color.White;
            btnResize.BackColor = Color.Black;
            btnUpscale.ForeColor = Color.White;
            btnUpscale.BackColor = Color.Black;
            btnDifuse.ForeColor = Color.White;
            btnDifuse.BackColor = Color.Black;
            btnLinear.ForeColor = Color.White;
            btnLinear.BackColor = Color.Black;
            btnDifuse2.ForeColor = Color.White;
            btnDifuse2.BackColor = Color.Black;
            btnLinear2.ForeColor = Color.White;
            btnLinear2.BackColor = Color.Black;
            BtnOpen2.ForeColor = Color.White;
            BtnOpen2.BackColor = Color.Black;
            BtnOpen3.ForeColor = Color.White;
            BtnOpen3.BackColor = Color.Black;
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "Image Files|*.png;*.dds";
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    selectedImagePath = openFileDialog.FileName;
                    Texdiag(selectedImagePath);
                    LogData("-----------------------------------------");
                    // Cek jika file adalah .dds, dan konversi ke .png
                    if (Path.GetExtension(selectedImagePath).ToLower() == ".dds")
                    {
                        string pngPath = Path.ChangeExtension(selectedImagePath, ".png");
                        string pngDir = Path.GetDirectoryName(selectedImagePath);
                        ConvertDDSToPNG(selectedImagePath, pngDir, pngPath);
                        selectedImagePath = pngPath; // Setelah konversi, pilih file .png
                        Texdiag(selectedImagePath);
                        LogData("-----------------------------------------");                        
                    }
                    // Ambil dimensi gambar (fast header read for large PNGs)
                    UpdateDimensionFieldsFast(selectedImagePath);
Log($"\nSelected image: {selectedImagePath}");
                    txtName.Text = Path.GetFileName(selectedImagePath);
                    txtName.SelectionStart = txtName.Text.Length;
                    txtName.ScrollToCaret();
                }
                else
                {
                    Log("\nImage selection canceled.");
                }
            }
        }
        // Fungsi untuk memproses gambar
        private async void BtnResize_Click(object sender, EventArgs e)
        {
            btnResize.ForeColor = Color.White;
            btnResize.BackColor = Color.Black;
            btnUpscale.ForeColor = Color.White;
            btnUpscale.BackColor = Color.Black;
            btnDifuse.ForeColor = Color.White;
            btnDifuse.BackColor = Color.Black;
            btnLinear.ForeColor = Color.White;
            btnLinear.BackColor = Color.Black;
            btnDifuse2.ForeColor = Color.White;
            btnDifuse2.BackColor = Color.Black;
            btnLinear2.ForeColor = Color.White;
            btnLinear2.BackColor = Color.Black;
            BtnOpen2.ForeColor = Color.White;
            BtnOpen2.BackColor = Color.Black;
            BtnOpen3.ForeColor = Color.White;
            BtnOpen3.BackColor = Color.Black;
            if (string.IsNullOrEmpty(selectedImagePath))
            {
                Log("\nPlease select an image first.\n");
                return;
            }
            if (!int.TryParse(txtWidth.Text, out int newWidth) || !int.TryParse(txtHeight.Text, out int newHeight))
            {
                Log("\nEnter a valid number for width and height.\n");
                return;
            }
            await RunResizeJobAsync(newWidth, newHeight);
        }
        // Fungsi untuk menjalankan perintah ImageMagick
        private void ExecuteMagickCommand(string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ImageMagick\\magick",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var process = Process.Start(startInfo))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (!string.IsNullOrEmpty(output))
                {
                    Log($"\nOutput: {output}");
                }
                if (!string.IsNullOrEmpty(error))
                {
                    Log($"\nError: {error}");
                }
                if (process.ExitCode != 0)
                {
                    throw new Exception($"ImageMagick command failed: {error}");
                }
            }
        }
        private void ExecuteTexconvCommand(string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "texconv.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var process = Process.Start(startInfo))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (!string.IsNullOrEmpty(output))
                {
                    var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    var filteredLines = lines.Skip(2); // Lewatkan 2 baris pertama
                    string cleanedOutput = string.Join(Environment.NewLine, filteredLines);
                    if (!string.IsNullOrWhiteSpace(cleanedOutput))
                    {
                        Log($"\nOutput: {cleanedOutput}");
                        Log("----------------------------------------------------------------------------------------");
                    }
                }
                if (!string.IsNullOrEmpty(error))
                {
                    Log($"\nError: {error}");
                }
                if (process.ExitCode != 0)
                {
                    throw new Exception($"Texconv command failed: {error}");
                }
            }
        }
        private void ExecuteTexdiag(string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "texdiag.exe",
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using (var process = Process.Start(startInfo))
            {
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (!string.IsNullOrEmpty(output))
                {
                    var lines = output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    var filteredLines = lines.Skip(2); // Lewatkan 2 baris pertama
                    string cleanedOutput = string.Join(Environment.NewLine, filteredLines);
                    // Ambil nilai dari "format = ..."
                    
                    foreach (var line in filteredLines)
                    {
                        if (line.TrimStart().StartsWith("format"))
                        {
                            var parts = line.Split('=');
                            if (parts.Length == 2)
                            {
                                formatValue = parts[1].Trim();
                                break;
                            }
                        }
                    }
                    SRGBcheck.Checked = formatValue?.ToUpper().Contains("SRGB") == true;                    
                    upstat = SRGBcheck.Checked ? "" : "-srgbi";
                    upstat2 = SRGBcheck.Checked ? "-srgbo" : "";
                    LogData($"\nOutput: {cleanedOutput}");
                }
                if (!string.IsNullOrEmpty(error))
                {
                    LogData($"\nError: {error}");
                }
                if (process.ExitCode != 0)
                {
                    throw new Exception($"Texdiag command failed: {error}");
                }
            }
        }
        // Fungsi untuk mengonversi .dds ke .png menggunakan ImageMagick
        private void ConvertDDSToPNG(string ddsPath, string pngPath, string pngLocation)
        {
            try
            {
                //ExecuteMagickCommand($"\"{ddsPath}\" \"{pngPath}\"");
                ExecuteTexconvCommand($" -ft PNG -o \"{pngPath}\"  \"{ddsPath}\" -y");
                Log($"\n");
                Log($"\nDDS file has been converted to PNG: {pngLocation}");
                Log($"\n");
            }
            catch (Exception ex)
            {
                Log($"\n");
                Log($"\nFailed to convert DDS to PNG: {ex.Message}");
                Log($"\n");
            }
        }
        private void Texdiag(string ddsPath)
        {
            try
            {
                //ExecuteMagickCommand($"\"{ddsPath}\" \"{pngPath}\"");
                ExecuteTexdiag($" info  \"{ddsPath}\" ");                
            }
            catch (Exception ex)
            {              
                LogData($"\nFailed to check info data : {ex.Message}");                
            }
        }
        // Thread-safe logging helpers
        private void AppendLineToTextBox(System.Windows.Forms.TextBox tb, string message)
        {
            if (tb == null) return;
            if (tb.InvokeRequired)
            {
                try { tb.BeginInvoke(new Action(() => tb.AppendText(message + Environment.NewLine))); }
                catch { /* ignore during shutdown */ }
                return;
            }
            tb.AppendText(message + Environment.NewLine);
        }
        // Normal log (suppressed during jobs)
        private void Log(string message)
        {
            if (_suppressJobLog) return;
            AppendLineToTextBox(txtLog, message ?? string.Empty);
        }
        // Important log (always shown, even during jobs)
        private void LogImportant(string message)
        {
            AppendLineToTextBox(txtLog, message ?? string.Empty);
        }
        private void LogData(string message)
        {
            AppendLineToTextBox(txtData, message ?? string.Empty);
        }
        // Convert current PNG to DDS (sRGB)
        private async void BtnDifuse_Click(object sender, EventArgs e)
        {
            await RunPngToDdsJobAsync(srgb: true);
        }
        // Convert current PNG to DDS (Linear)
        private async void BtnLinear_Click(object sender, EventArgs e)
        {
            await RunPngToDdsJobAsync(srgb: false);
        }
        private string EnsurePngFitsDdsLimit(string inputPngPath, int maxDim, out string resizeLogMessage)
        {
            resizeLogMessage = null;
            if (string.IsNullOrWhiteSpace(inputPngPath)) return inputPngPath;
            if (!File.Exists(inputPngPath)) return inputPngPath;
            if (!Path.GetExtension(inputPngPath).Equals(".png", StringComparison.OrdinalIgnoreCase)) return inputPngPath;
            if (!TryGetPngDimensions(inputPngPath, out int w, out int h)) return inputPngPath;
            if (w <= maxDim && h <= maxDim) return inputPngPath;
            string dir = Path.GetDirectoryName(inputPngPath);
            string baseName = Path.GetFileNameWithoutExtension(inputPngPath);
            // Write fit PNG into a unique temp folder but keep the same base filename so texconv outputs the expected DDS name.
            string fitDir = Path.Combine(dir, "TempFit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fitDir);
            string fitPng = Path.Combine(fitDir, baseName + ".png");
            // IMPORTANT: preserve RGB data under fully-transparent pixels.
            // A normal resize on an image with alpha may premultiply against black and destroy hidden RGB.
            // We resize RGB and alpha separately and then re-composite.
            string fitOpaque = Path.Combine(fitDir, baseName + "_fit_opaque.png");
            string fitMask = Path.Combine(fitDir, baseName + "_fit_mask.png");
            try
            {
                RunMagickOrThrow($"\"{inputPngPath}\" -alpha off -resize {maxDim}x{maxDim} \"{fitOpaque}\"", fitDir);
                RunMagickOrThrow($"\"{inputPngPath}\" -alpha extract -resize {maxDim}x{maxDim} \"{fitMask}\"", fitDir);
                RunMagickOrThrow($"\"{fitOpaque}\" \"{fitMask}\" -alpha off -compose CopyOpacity -composite \"{fitPng}\"", fitDir);
            }
            catch
            {
                // cleanup
                try { Directory.Delete(fitDir, true); } catch { }
                throw;
            }
            int nw = 0, nh = 0;
            if (!TryGetPngDimensions(fitPng, out nw, out nh)) { nw = Math.Min(w, maxDim); nh = Math.Min(h, maxDim); }
            resizeLogMessage = $"Output exceeds DirectX DDS max size ({maxDim}). Auto-resized {w}x{h} -> {nw}x{nh} for DDS conversion.";
            return fitPng;
        }
        private void RunMagickOrThrow(string arguments, string fitTempDirForContext)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine("ImageMagick", "magick"),
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Application.StartupPath
            };
            using (var proc = Process.Start(startInfo))
            {
                string outp = proc.StandardOutput.ReadToEnd();
                string err = proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                {
                    string msg = string.IsNullOrWhiteSpace(err) ? outp : err;
                    throw new Exception("ImageMagick resize failed: " + msg);
                }
            }
        }
        private void CleanupFitTemp(string fitPngPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fitPngPath)) return;
                string dir = Path.GetDirectoryName(fitPngPath);
                if (string.IsNullOrWhiteSpace(dir)) return;
                // fitPngPath is inside TempFit_<guid>
                if (Path.GetFileName(dir).StartsWith("TempFit_", StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch { }
        }
// Fungsi untuk mendapatkan nama file output yang benar
        private string GetOutputFilePath(string inputPath, string suffix)
        {
            string directory = Path.GetDirectoryName(inputPath);
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(inputPath);
            return Path.Combine(directory, fileNameWithoutExtension + suffix);
        }
        // Fungsi untuk mengonversi gambar ke DDS menggunakan ImageMagick
        private void ConvertToDDS(string inputPath, string outputPath, string compression)
        {
            string fitPng = null;
            try
            {
                // Option B: if the PNG exceeds the DirectX DDS max dimension, auto-resize to fit.
                string resizeMsg;
                fitPng = EnsurePngFitsDdsLimit(inputPath, 16384, out resizeMsg);
                if (!string.IsNullOrWhiteSpace(resizeMsg))
                {
                    LogImportant("\n" + resizeMsg + "\n");
                }
                string inputForTexconv = fitPng ?? inputPath;
                var startInfo = new ProcessStartInfo
                {
                    FileName = "texconv.exe",
                    Arguments = $"\"{inputForTexconv}\" {compression} -o \"{outputPath}\" -y",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = Application.StartupPath
                };
                using (var process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if (process.ExitCode == 0)
                    {
                        LogImportant("\nDDS conversion complete.\n");
                    }
                    else
                    {
                        throw new Exception(string.IsNullOrWhiteSpace(error) ? output : error);
                    }
                }
            }
            catch (Exception ex)
            {
                LogImportant("\nFailed to convert to DDS: " + ex.Message + "\n");
            }
            finally
            {
                // Delete temporary fit image unless user wants to keep temp files.
                if (!checkTemp.Checked && fitPng != null && !string.Equals(fitPng, inputPath, StringComparison.OrdinalIgnoreCase))
                {
                    CleanupFitTemp(fitPng);
                }
            }
        }
        // ------------------------------
        // Cancellable PNG -> DDS conversion (runs on background thread via RunPngToDdsJobAsync)
        // ------------------------------
        private string EnsurePngFitsDdsLimitCancellable(string inputPngPath, int maxDim, CancellationToken token, out string resizeLogMessage)
        {
            resizeLogMessage = null;
            if (string.IsNullOrWhiteSpace(inputPngPath)) return inputPngPath;
            if (!File.Exists(inputPngPath)) return inputPngPath;
            if (!Path.GetExtension(inputPngPath).Equals(".png", StringComparison.OrdinalIgnoreCase)) return inputPngPath;
            if (!TryGetPngDimensions(inputPngPath, out int w, out int h)) return inputPngPath;
            if (w <= maxDim && h <= maxDim) return inputPngPath;
            string dir = Path.GetDirectoryName(inputPngPath);
            string baseName = Path.GetFileNameWithoutExtension(inputPngPath);
            // Write fit PNG into a unique temp folder but keep the same base filename so texconv outputs the expected DDS name.
            string fitDir = Path.Combine(dir, "TempFit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fitDir);
            string fitOpaque = Path.Combine(fitDir, baseName + "_opaque.png");
            string fitMask = Path.Combine(fitDir, baseName + "_mask.png");
            string fitOpaqueUpscaled = Path.Combine(fitDir, baseName + "_opaque_fit.png");
            string fitMaskUpscaled = Path.Combine(fitDir, baseName + "_mask_fit.png");
            string fitPng = Path.Combine(fitDir, baseName + ".png");
            try
            {
                token.ThrowIfCancellationRequested();
                ExecuteMagickCommandCancellable($"\"{inputPngPath}\" -alpha off \"{fitOpaque}\"", token);
                token.ThrowIfCancellationRequested();
                ExecuteMagickCommandCancellable($"\"{inputPngPath}\" -alpha extract \"{fitMask}\"", token);
                // Resize each channel independently so we keep alpha intact.
                token.ThrowIfCancellationRequested();
                ExecuteMagickCommandCancellable($"\"{fitOpaque}\" -resize {maxDim}x{maxDim} \"{fitOpaqueUpscaled}\"", token);
                token.ThrowIfCancellationRequested();
                ExecuteMagickCommandCancellable($"\"{fitMask}\" -resize {maxDim}x{maxDim} \"{fitMaskUpscaled}\"", token);
                token.ThrowIfCancellationRequested();
                ExecuteMagickCommandCancellable($"\"{fitOpaqueUpscaled}\" \"{fitMaskUpscaled}\" -alpha off -compose CopyOpacity -composite \"{fitPng}\"", token);
            }
            catch
            {
                try { Directory.Delete(fitDir, true); } catch { }
                throw;
            }
            int nw = 0, nh = 0;
            if (!TryGetPngDimensions(fitPng, out nw, out nh)) { nw = Math.Min(w, maxDim); nh = Math.Min(h, maxDim); }
            resizeLogMessage = $"Output exceeds DirectX DDS max size ({maxDim}). Auto-resized {w}x{h} -> {nw}x{nh} for DDS conversion.";
            return fitPng;
        }
        private (bool IsSrgb, string CleanedOutput) TexdiagInfoCancellable(string ddsPath, CancellationToken token)
        {
            var stdout = new List<string>();
            var stderr = new List<string>();
            int code = RunProcessStreaming("texdiag.exe", $" info \"{ddsPath}\" ", token,
                line => { if (line != null) stdout.Add(line); },
                line => { if (line != null) stderr.Add(line); });
            if (code != 0)
            {
                string err = string.Join(Environment.NewLine, stderr).Trim();
                if (string.IsNullOrWhiteSpace(err)) err = string.Join(Environment.NewLine, stdout).Trim();
                throw new Exception("Texdiag command failed: " + err);
            }
            var filtered = stdout.Count > 2 ? stdout.GetRange(2, stdout.Count - 2) : new List<string>();
            string cleaned = string.Join(Environment.NewLine, filtered);
            string fmt = null;
            foreach (var line in filtered)
            {
                if (line == null) continue;
                if (line.TrimStart().StartsWith("format", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split('=');
                    if (parts.Length == 2)
                    {
                        fmt = parts[1].Trim();
                        break;
                    }
                }
            }
            bool isSrgb = fmt?.ToUpper().Contains("SRGB") == true;
            return (isSrgb, cleaned);
        }
        private string ConvertPngToDdsCancellable(string inputPngPath, bool srgb, CancellationToken token)
        {
            string fitPng = null;
            try
            {
                string resizeMsg;
                fitPng = EnsurePngFitsDdsLimitCancellable(inputPngPath, 16384, token, out resizeMsg);
                if (!string.IsNullOrWhiteSpace(resizeMsg))
                {
                    LogImportant("\n" + resizeMsg + "\n");
                }
                string inputForTexconv = fitPng ?? inputPngPath;
                string outDir = Path.GetDirectoryName(inputPngPath);
                // Use separate alpha so we preserve the original alpha channel.
                string args;
                if (srgb)
                    args = $"\"{inputForTexconv}\" -f BC{bc}_UNORM_SRGB {upstat} -nologo --separate-alpha -o \"{outDir}\" -y";
                else
                    args = $"\"{inputForTexconv}\" -f BC{bc}_UNORM {upstat2} -nologo --separate-alpha -o \"{outDir}\" -y";
                ExecuteTexconvCommandCancellable(args, token, logFilteredOutput: false);
                // texconv outputs <base>.dds into outDir
                return Path.Combine(outDir, Path.GetFileNameWithoutExtension(inputPngPath) + ".dds");
            }
            finally
            {
                if (!checkTemp.Checked && fitPng != null && !string.Equals(fitPng, inputPngPath, StringComparison.OrdinalIgnoreCase))
                {
                    CleanupFitTemp(fitPng);
                }
            }
        }
        private void ResetActionButtonColors()
        {
            try
            {
                btnResize.ForeColor = Color.White;
                btnResize.BackColor = Color.Black;
                btnUpscale.ForeColor = Color.White;
                btnUpscale.BackColor = Color.Black;
                btnDifuse.ForeColor = Color.White;
                btnDifuse.BackColor = Color.Black;
                btnLinear.ForeColor = Color.White;
                btnLinear.BackColor = Color.Black;
                btnDifuse2.ForeColor = Color.White;
                btnDifuse2.BackColor = Color.Black;
                btnLinear2.ForeColor = Color.White;
                btnLinear2.BackColor = Color.Black;
                BtnOpen2.ForeColor = Color.White;
                BtnOpen2.BackColor = Color.Black;
                BtnOpen3.ForeColor = Color.White;
                BtnOpen3.BackColor = Color.Black;
            }
            catch { }
        }
        private async Task RunPngToDdsJobAsync(bool srgb)
        {
            ResetActionButtonColors();
            if (string.IsNullOrEmpty(selectedImagePath))
            {
                Log("\nPlease select an image first.\n");
                return;
            }
            if (!Path.GetExtension(selectedImagePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                Log("\nSelected image is not .png\n");
                return;
            }
            if (_jobRunning)
            {
                Log("\nA job is already running.\n");
                return;
            }
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            // Indeterminate progress for DDS conversion
            SetJobUiState(true, srgb ? "Converting to DDS (sRGB)..." : "Converting to DDS (Linear)...");
            await Task.Yield();
            _suppressJobLog = true;
            if (progressBarJob != null)
            {
                progressBarJob.Style = ProgressBarStyle.Marquee;
                progressBarJob.MarqueeAnimationSpeed = 30;
            }
            if (lblPercent != null) lblPercent.Text = "";
            LogImportant("----------------------------------------------------------------------------------------");
            LogImportant($"START: Convert {Path.GetFileName(selectedImagePath)} -> DDS {(srgb ? "sRGB" : "Linear")}");
            try
            {
                string input = selectedImagePath;
                // Run conversion + texdiag off the UI thread.
                var result = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    string ddsPath = ConvertPngToDdsCancellable(input, srgb, token);
                    token.ThrowIfCancellationRequested();
                    var info = TexdiagInfoCancellable(ddsPath, token);
                    return (ddsPath, info.IsSrgb, info.CleanedOutput);
                });
                selectedImagePath = result.ddsPath;
                // Update SRGB checkbox + flags
                formatValue = result.IsSrgb ? "SRGB" : "";
                SRGBcheck.Checked = result.IsSrgb;
                upstat = SRGBcheck.Checked ? "" : "-srgbi";
                upstat2 = SRGBcheck.Checked ? "-srgbo" : "";
                // Update info panel
                if (!string.IsNullOrWhiteSpace(result.CleanedOutput))
                {
                    LogData($"\nOutput: {result.CleanedOutput}");
                    LogData("-----------------------------------------");
                }
                txtName.Text = Path.GetFileName(selectedImagePath);
                txtName.SelectionStart = txtName.Text.Length;
                txtName.ScrollToCaret();
                LogImportant($"DONE: Converted output {selectedImagePath}");
                // UI highlight
                if (srgb)
                {
                    btnDifuse.ForeColor = Color.Black;
                    btnDifuse.BackColor = Color.Lime;
                }
                else
                {
                    btnLinear.ForeColor = Color.Black;
                    btnLinear.BackColor = Color.Lime;
                }
                BtnOpen2.ForeColor = Color.Black;
                BtnOpen2.BackColor = Color.Lime;
                BtnOpen3.ForeColor = Color.Black;
                BtnOpen3.BackColor = Color.Lime;
                PlayCompletionSound();
            }
            catch (OperationCanceledException)
            {
                LogImportant("Canceled.");
            }
            catch (Exception ex)
            {
                LogImportant("ERROR: " + ex.Message);
            }
            finally
            {
                // restore progress bar
                if (progressBarJob != null)
                {
                    progressBarJob.Style = ProgressBarStyle.Blocks;
                    progressBarJob.MarqueeAnimationSpeed = 0;
                    progressBarJob.Value = 0;
                }
                if (lblPercent != null) lblPercent.Text = "";
                _suppressJobLog = false;
                SetJobUiState(false, "Idle");
                try { _cts?.Dispose(); } catch { }
                _cts = null;
            }
        }
        private void RadX2_CheckedChanged(object sender, EventArgs e)
        {
            imgscale = "2";
            dataimg = "realesr-animevideov3";
            Log("X2 UPSCALE Selected");
        }
        private void RadX3_CheckedChanged(object sender, EventArgs e)
        {
            imgscale = "3";
            dataimg = "realesr-animevideov3";
            Log("X3 UPSCALE Selected");
        }
        private void RadX4_CheckedChanged(object sender, EventArgs e)
        {
            imgscale = "4";
            dataimg = "realesrgan-x4plus-anime";
            Log("X4 UPSCALE Selected");
        }
        private async void BtnUpscale_Click(object sender, EventArgs e)
        {
            // Keep existing UI color reset logic
            btnResize.ForeColor = Color.White;
            btnResize.BackColor = Color.Black;
            btnUpscale.ForeColor = Color.White;
            btnUpscale.BackColor = Color.Black;
            btnUpscale2.ForeColor = Color.White;
            btnUpscale2.BackColor = Color.Black;
            btnDifuse.ForeColor = Color.White;
            btnDifuse.BackColor = Color.Black;
            btnLinear.ForeColor = Color.White;
            btnLinear.BackColor = Color.Black;
            btnDifuse2.ForeColor = Color.White;
            btnDifuse2.BackColor = Color.Black;
            btnLinear2.ForeColor = Color.White;
            btnLinear2.BackColor = Color.Black;
            BtnOpen2.ForeColor = Color.White;
            BtnOpen2.BackColor = Color.Black;
            BtnOpen3.ForeColor = Color.White;
            BtnOpen3.BackColor = Color.Black;
            await RunSingleUpscaleJobAsync();
        }
        private void panel1_Paint(object sender, PaintEventArgs e)
        {
        }
        private void label2_Click(object sender, EventArgs e)
        {
        }
        private void BtnSwap1_Click(object sender, EventArgs e)
        {
            BtnSwap1.FlatStyle = FlatStyle.Flat;
            BtnSwap2.FlatStyle = FlatStyle.Popup;
            BtnSwap3.FlatStyle = FlatStyle.Popup;
            panelResize.BringToFront();
            comboBox1.BringToFront();
            label7.BringToFront();
            SRGBcheck.BringToFront();
            label8.BringToFront();
            txtName.BringToFront();
        }
        private void BtnSwap2_Click(object sender, EventArgs e)
        {
            BtnSwap2.FlatStyle = FlatStyle.Flat;
            BtnSwap1.FlatStyle = FlatStyle.Popup;
            BtnSwap3.FlatStyle = FlatStyle.Popup;
            panelUpscale.BringToFront();
            comboBox1.BringToFront();
            label7.BringToFront();
            SRGBcheck.BringToFront();
            label8.BringToFront();
            txtName.BringToFront();
        }
        private void BtnSwap3_Click(object sender, EventArgs e)
        {
            BtnSwap3.FlatStyle = FlatStyle.Flat;
            BtnSwap1.FlatStyle = FlatStyle.Popup;
            BtnSwap2.FlatStyle = FlatStyle.Popup;
            panelBatch.BringToFront();
            comboBox1.BringToFront();
            label7.BringToFront();
            SRGBcheck.BringToFront();
            label8.BringToFront();
            txtName.BringToFront();
        }
        private void checkSound_CheckedChanged(object sender, EventArgs e)
        {
            SaveSettings();
        }
        private void linkLabel1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            string url = "https://github.com/eroge69/TextureUpscaler";
            try
            {
                // Membuka URL di browser default
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true // Menyuruh sistem untuk membuka URL dengan browser
                });
            }
            catch (Exception ex)
            {
                Log($"\n");
                Log($"\nAn error occurred: {ex.Message}");
            }
        }
        private void checkTemp_CheckedChanged(object sender, EventArgs e)
        {
            SaveSettings();
        }
        private void CheckTop_CheckedChanged(object sender, EventArgs e)
        {
            this.TopMost = checkTop.Checked;
            SaveSettings();
        }
        private void Form1_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] paths = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (paths.Length > 0)
                {
                    string path = paths[0];
                    if (Directory.Exists(path))
                    {
                        // Kalau folder, ijinkan drop
                        e.Effect = DragDropEffects.Copy;
                    }
                    else
                    {
                        string ext = Path.GetExtension(path).ToLower();
                        if (ext == ".png" || ext == ".dds")
                        {
                            e.Effect = DragDropEffects.Copy;
                        }
                        else
                        {
                            e.Effect = DragDropEffects.None;
                        }
                    }
                }
                else
                {
                    e.Effect = DragDropEffects.None;
                }
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }
        private void Form1_DragDrop(object sender, DragEventArgs e)
        {
            txtData.Clear();
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0)
            {
                string path = files[0];
                if (Directory.Exists(path))
                {
                    //Jika yang di-drop adalah folder
                    HandleFolderDrop(path);
                }
                else
                {
                    //Jika file tunggal (png/dds)
                    selectedImagePath = path;
                    string fileExtension = Path.GetExtension(selectedImagePath).ToLower();
                    if (fileExtension == ".png" || fileExtension == ".dds")
                    {
                        if (fileExtension == ".dds")
                        {
                            Texdiag(selectedImagePath);
                            LogData("-----------------------------------------");
                            string pngPath = Path.ChangeExtension(selectedImagePath, ".png");
                            string pngDir = Path.GetDirectoryName(selectedImagePath);
                            ConvertDDSToPNG(selectedImagePath, pngDir, pngPath);
                            selectedImagePath = pngPath;
                        }
                        Texdiag(selectedImagePath);
                        LogData("-----------------------------------------");
                        Log($"\nImage selected: {selectedImagePath}");
                        txtName.Text = Path.GetFileName(selectedImagePath);
                        txtName.SelectionStart = txtName.Text.Length;
                        txtName.ScrollToCaret();
                        panelBatch.SendToBack();
                        Log($"\n");
                    }
                    else
                    {
                        Log($"\nOnly PNG || DDS files are allowed.\n");
                    }
                }
            }
        }
        private void HandleFolderDrop(string folderPath)
        {
            selectedFolderPath = folderPath;
            Log($"\nSelected folder: {selectedFolderPath}");
            txtName.Text = Path.GetFileName(selectedFolderPath);
            txtName.SelectionStart = txtName.Text.Length;
            txtName.ScrollToCaret();
            panelBatch.BringToFront();
            txtName.BringToFront();
            Log($"\n");
            
        }
        private void BtnOpen_Click(object sender, EventArgs e)
        {
            btnResize.ForeColor = Color.White;
            btnResize.BackColor = Color.Black;
            btnUpscale.ForeColor = Color.White;
            btnUpscale.BackColor = Color.Black;
            btnDifuse.ForeColor = Color.White;
            btnDifuse.BackColor = Color.Black;
            btnLinear.ForeColor = Color.White;
            btnLinear.BackColor = Color.Black;
            btnDifuse2.ForeColor = Color.White;
            btnDifuse2.BackColor = Color.Black;
            btnLinear2.ForeColor = Color.White;
            btnLinear2.BackColor = Color.Black;
            BtnOpen2.ForeColor = Color.White;
            BtnOpen2.BackColor = Color.Black;
            try
            {
                if (!string.IsNullOrEmpty(selectedImagePath) && File.Exists(selectedImagePath))
                {
                    // Membuka lokasi file di File Explorer dan memfokuskan file
                    Process.Start("explorer.exe", $"/select, \"{selectedImagePath}\"");
                }
                else
                {
                    MessageBox.Show("File path is invalid || file does not exist.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private string selectedFolderPath = string.Empty;
            private void btnSelectFolder_Click(object sender, EventArgs e)
        {
            // Preserve size/location to avoid any shell-dialog autoscale glitches ("window shrinks" bug).
            var sizeBefore = this.Size;
            var locationBefore = this.Location;
            var stateBefore = this.WindowState;
            txtData.Clear();
            try
            {
                string initial = (!string.IsNullOrWhiteSpace(selectedFolderPath) && Directory.Exists(selectedFolderPath))
                    ? selectedFolderPath
                    : null;
                if (FolderPicker.TryPickFolder(this.Handle, initial, out var picked) && Directory.Exists(picked))
                {
                    selectedFolderPath = picked;
                    LogImportant($"\nSelected folder: {selectedFolderPath}\n");
                    txtName.Text = Path.GetFileName(selectedFolderPath);
                    txtName.SelectionStart = txtName.Text.Length;
                    txtName.ScrollToCaret();
                }
                else
                {
                    LogImportant("\nFolder selection canceled.\n");
                }
            }
            finally
            {
                // Restore original window metrics in case the common dialog caused any transient scaling.
                try
                {
                    if (stateBefore == FormWindowState.Normal)
                    {
                        this.Size = sizeBefore;
                        this.Location = locationBefore;
                    }
                    else
                    {
                        this.WindowState = stateBefore;
                    }
                }
                catch { }
                // Ensure layout stays consistent with our non-resizable UI.
                try { ApplyResponsiveLayout(); } catch { }
            }
        }
        private bool CheckOverwriteWarning(string outputFolder)
        {
            var files = Directory.GetFiles(outputFolder, "*", SearchOption.TopDirectoryOnly);
            if (files.Length > 0)
            {
                var result = MessageBox.Show("The 'UPSCALED' folder already contains files. Do you want to continue && overwrite the existing files?", "Warning", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                return result == DialogResult.Yes;
            }
            return true;
        }
        private async void btnUpscale2_Click(object sender, EventArgs e)
        {
            btnUpscale2.ForeColor = Color.White;
            btnUpscale2.BackColor = Color.Black;
            btnDifuse3.ForeColor = Color.White;
            btnDifuse3.BackColor = Color.Black;
            btnLinear3.ForeColor = Color.White;
            btnLinear3.BackColor = Color.Black;
            BtnOpen1.ForeColor = Color.White;
            BtnOpen1.BackColor = Color.Black;
            pngCheck.CheckState = CheckState.Unchecked;
            await RunBatchUpscaleJobAsync();
        }
        private string GetRelativePath(string basePath, string targetPath)
        {
            Uri baseUri = new Uri(AppendDirectorySeparatorChar(basePath));
            Uri targetUri = new Uri(targetPath);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(targetUri).ToString().Replace('/', Path.DirectorySeparatorChar));
        }
        private string AppendDirectorySeparatorChar(string path)
        {
            if (!path.EndsWith(Path.DirectorySeparatorChar.ToString()))
                return path + Path.DirectorySeparatorChar;
            return path;
        }
        private void ExecuteRealesrgan(string input, string output)
        {
            ProcessStartInfo ps = new ProcessStartInfo
            {
                FileName = "Realesrgan\\realesrgan-ncnn-vulkan",
                Arguments = $"-i \"{input}\" -n {dataimg} -s {imgscale} -o \"{output}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using (var process = Process.Start(ps))
            {
                process.WaitForExit();
            }
        }
        private void BtnOpen1_Click(object sender, EventArgs e)
        {
            
            btnUpscale2.ForeColor = Color.White;
            btnUpscale2.BackColor = Color.Black;
            btnDifuse3.ForeColor = Color.White;
            btnDifuse3.BackColor = Color.Black;
            btnLinear3.ForeColor = Color.White;
            btnLinear3.BackColor = Color.Black;
            BtnOpen1.ForeColor = Color.White;
            BtnOpen1.BackColor = Color.Black;
            try
            {
                if (!string.IsNullOrEmpty(selectedFolderPath) && Directory.Exists(selectedFolderPath))
                {
                    // Membuka folder di File Explorer
                    Process.Start("explorer.exe", $"\"{selectedFolderPath}\"");
                }
                else
                {
                    MessageBox.Show("Folder path is invalid || does not exist.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"An error occurred: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        private async void btnDifuse3_Click(object sender, EventArgs e)
{
    await RunBatchPngToDdsAsync(srgb: true);
}
        private async void btnLinear3_Click(object sender, EventArgs e)
{
    await RunBatchPngToDdsAsync(srgb: false);
}
        private void comboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            bc = comboBox1.SelectedItem.ToString();
        }
        private void SRGBcheck_CheckedChanged(object sender, EventArgs e)
        {
            SRGBcheck.Checked = formatValue?.ToUpper().Contains("SRGB") == true;
        }
        
        // ------------------------------
        // Async job helpers (no UI freeze, cancelable, progress)
        // ------------------------------
        private void SetJobUiState(bool running, string status)
        {
            _jobRunning = running;
            // these controls are added in the patched Designer
            if (lblStatus != null) lblStatus.Text = status ?? (running ? "Working..." : "Idle");
            if (progressBarJob != null)
            {
                progressBarJob.Visible = true;
                if (!running) { progressBarJob.Style = ProgressBarStyle.Blocks; progressBarJob.MarqueeAnimationSpeed = 0; progressBarJob.Value = 0; }
            }
            if (lblPercent != null)
            {
                lblPercent.Text = running ? "0%" : "";
            }
            if (btnCancel != null) btnCancel.Enabled = running;
            // Disable main panels (keeps visuals stable; Cancel remains active)
            try
            {
                if (panelResize != null) panelResize.Enabled = !running;
                if (panelUpscale != null) panelUpscale.Enabled = !running;
                if (panelBatch != null) panelBatch.Enabled = !running;
                // These are the user option checkboxes declared in Form1.Designer.cs
                if (checkSound != null) checkSound.Enabled = !running;
                if (checkTemp != null) checkTemp.Enabled = !running;
                if (checkTop != null) checkTop.Enabled = !running;
                if (comboBox1 != null) comboBox1.Enabled = !running;
            }
            catch { }
            // Wait cursor hint
            try { this.UseWaitCursor = running; Application.UseWaitCursor = running; } catch { }
        }
        private void ReportProgress(IProgress<JobProgress> progress, int percent, string status)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            progress?.Report(new JobProgress { Percent = percent, Status = status });
        }
        private void WireProgressToUi(IProgress<JobProgress> progress)
        {
            // This is called by handlers (on UI thread). The Progress<T> implementation
            // already marshals to the UI thread.
        }
        private int RunProcessStreaming(
            string fileName,
            string arguments,
            CancellationToken token,
            Action<string> onStdoutLine = null,
            Action<string> onStderrLine = null)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Application.StartupPath
            };
            using (var process = new Process { StartInfo = startInfo })
            {
                process.OutputDataReceived += (s, e) =>
                {
                    if (e.Data != null) onStdoutLine?.Invoke(e.Data);
                };
                process.ErrorDataReceived += (s, e) =>
                {
                    if (e.Data != null) onStderrLine?.Invoke(e.Data);
                };
                process.Start();
            try
            {
                var exe = Path.GetFileName(fileName);
                if (string.Equals(exe, "texconv.exe", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(exe, "texdiag.exe", StringComparison.OrdinalIgnoreCase))
                {
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }
            }
            catch { /* ignore */ }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                // Poll so we can honor cancellation without deadlocking stdout/stderr.
                while (!process.WaitForExit(200))
                {
                    if (token.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { /* ignore */ }
                        token.ThrowIfCancellationRequested();
                    }
                }
                // Ensure async output flush.
                process.WaitForExit();
                if (token.IsCancellationRequested)
                {
                    token.ThrowIfCancellationRequested();
                }
                return process.ExitCode;
            }
        }
        private static bool TryGetPngDimensions(string pngPath, out int width, out int height)
        {
            width = 0; height = 0;
            try
            {
                using (var fs = File.OpenRead(pngPath))
                {
                    // PNG signature (8 bytes) + IHDR length/type (8 bytes) + width/height (8 bytes)
                    var buf = new byte[24];
                    int read = fs.Read(buf, 0, buf.Length);
                    if (read < 24) return false;
                    // Signature
                    byte[] sig = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
                    for (int i = 0; i < 8; i++) if (buf[i] != sig[i]) return false;
                    // Chunk type should be IHDR at bytes 12..15
                    if (buf[12] != (byte)'I' || buf[13] != (byte)'H' || buf[14] != (byte)'D' || buf[15] != (byte)'R') return false;
                    width = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
                    height = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
                    return width > 0 && height > 0;
                }
            }
            catch
            {
                return false;
            }
        }
        private void UpdateDimensionFieldsFast(string imagePath)
        {
            try
            {
                if (Path.GetExtension(imagePath).Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryGetPngDimensions(imagePath, out int w, out int h))
                    {
                        txtWidth.Text = w.ToString();
                        txtHeight.Text = h.ToString();
                        return;
                    }
                }
                // Fallback (small images only)
                using (Image img = Image.FromFile(imagePath))
                {
                    txtWidth.Text = img.Width.ToString();
                    txtHeight.Text = img.Height.ToString();
                }
            }
            catch
            {
                // ignore
            }
        }
        private int GetRecommendedTileSize(int width, int height)
        {
            // Heuristic for VRAM safety on large textures.
            // For high-VRAM GPUs (e.g., 16GB), we can keep tiles larger for quality/perf,
            // but still force tiling on very large images to avoid stalls/OOM.
            int maxDim = Math.Max(width, height);
            if (maxDim <= 4096) return 0;      // auto (often fastest)
            if (maxDim <= 8192) return 512;
            if (maxDim <= 16384) return 256;
            if (maxDim <= 24576) return 128;
            return 64;
        }
        private static readonly Regex _tileRegex = new Regex(@"tile\s*(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private void ExecuteRealesrganWithProgress(
            string input,
            string output,
            int tileSize,
            CancellationToken token,
            IProgress<JobProgress> progress,
            int startPercent,
            int endPercent,
            string statusPrefix)
        {
            int lastPct = -1;
            var tail = new Queue<string>(64);
            Action<string> onLine = (line) =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (tail.Count >= 64) tail.Dequeue();
                    tail.Enqueue(line);
                }
                // Parse tile progress lines if present.
                var m = _tileRegex.Match(line ?? string.Empty);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int done) && int.TryParse(m.Groups[2].Value, out int total) && total > 0)
                {
                    double frac = Math.Max(0.0, Math.Min(1.0, done / (double)total));
                    int pct = startPercent + (int)Math.Round((endPercent - startPercent) * frac);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        ReportProgress(progress, pct, $"{statusPrefix} ({done}/{total} tiles)");
                    }
                    return;
                }
                // Suppress realtime process output in the log. Keep UI status/progress only.
            };
            var exe = Path.Combine("Realesrgan", "realesrgan-ncnn-vulkan");
            var args = new StringBuilder();
            args.Append($"-i \"{input}\" -n {dataimg} -s {imgscale} -o \"{output}\"");
            // tiling is the #1 fix for large textures
            args.Append($" -t {tileSize}");
            // threading (load:proc:save). Slightly more parallel by default.
            // If you see disk thrash or instability on low-end systems, revert to 1:2:2.
            args.Append(" -j 2:4:2");
            int code = RunProcessStreaming(exe, args.ToString(), token, onLine, onLine);
            if (code != 0)
            {
                throw new Exception($"Realesrgan failed (exit code {code}).\n" + string.Join("\n", tail));
            }
            ReportProgress(progress, endPercent, statusPrefix);
        }
        private void ExecuteMagickCommandCancellable(string arguments, CancellationToken token)
        {
            // magick can be noisy; log only errors
            var exe = Path.Combine("ImageMagick", "magick");
            var stderr = new List<string>();
            int code = RunProcessStreaming(exe, arguments, token, null, line => { if (!string.IsNullOrWhiteSpace(line)) stderr.Add(line); });
            if (code != 0)
            {
                throw new Exception("ImageMagick command failed: " + string.Join("\n", stderr));
            }
        }
        private void ExecuteTexconvCommandCancellable(string arguments, CancellationToken token, bool logFilteredOutput)
        {
            var exe = "texconv.exe";
            var stdoutLines = new List<string>();
            var stderrLines = new List<string>();
            int code = RunProcessStreaming(exe, arguments, token,
                line => { if (line != null) stdoutLines.Add(line); },
                line => { if (line != null) stderrLines.Add(line); });
            if (logFilteredOutput && stdoutLines.Count > 0)
            {
                // Mimic original behavior: skip first 2 lines
                var filtered = stdoutLines.Count > 2 ? stdoutLines.GetRange(2, stdoutLines.Count - 2) : new List<string>();
                string cleaned = string.Join(Environment.NewLine, filtered).Trim();
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    Log($"\nOutput: {cleaned}");
                    Log("----------------------------------------------------------------------------------------");
                }
            }
            if (stderrLines.Count > 0)
            {
                string err = string.Join(Environment.NewLine, stderrLines).Trim();
                if (!string.IsNullOrWhiteSpace(err)) Log($"\nError: {err}");
            }
            if (code != 0)
            {
                throw new Exception("Texconv command failed: " + string.Join("\n", stderrLines));
            }
        }
        private void ConvertDDSToPNG_Cancellable(string ddsPath, string outputDir, string pngLocation, CancellationToken token)
        {
            ExecuteTexconvCommandCancellable($" -ft PNG -o \"{outputDir}\"  \"{ddsPath}\" -y", token, logFilteredOutput: true);
            Log($"\nDDS file has been converted to PNG: {pngLocation}\n");
        }
        private string UpscaleSinglePng(string inputPngPath, CancellationToken token, IProgress<JobProgress> progress)
        {
            string directory = Path.GetDirectoryName(inputPngPath);
            string baseName = Path.GetFileNameWithoutExtension(inputPngPath);
            string opaquePath = Path.Combine(directory, $"{baseName}_opaque.png");
            string opaquePath2 = Path.Combine(directory, $"{baseName}_opaque_upscaled.png");
            string maskPath = Path.Combine(directory, $"{baseName}_mask.png");
            string maskPath2 = Path.Combine(directory, $"{baseName}_mask_upscaled.png");
            string finalPath = Path.Combine(directory, $"{baseName}_upscaled.png");
            bool keepTemp = checkTemp.Checked;
            try
            {
                ReportProgress(progress, 0, "Preparing...");
                token.ThrowIfCancellationRequested();
                ReportProgress(progress, 5, "Extracting opaque...");
                ExecuteMagickCommandCancellable($"\"{inputPngPath}\" -alpha off \"{opaquePath}\"", token);
                token.ThrowIfCancellationRequested();
                int w = 0, h = 0;
                if (!TryGetPngDimensions(opaquePath, out w, out h))
                {
                    // fallback small decode; should be rare
                    using (var img = Image.FromFile(opaquePath)) { w = img.Width; h = img.Height; }
                }
                int tile = GetRecommendedTileSize(w, h);
                token.ThrowIfCancellationRequested();
                ReportProgress(progress, 6, $"Upscaling opaque... (tile {tile})");
                ExecuteRealesrganWithProgress(opaquePath, opaquePath2, tile, token, progress, 6, 45, "Upscaling opaque");
                token.ThrowIfCancellationRequested();
                ReportProgress(progress, 46, "Extracting alpha mask...");
                ExecuteMagickCommandCancellable($"\"{inputPngPath}\" -alpha extract \"{maskPath}\"", token);
                token.ThrowIfCancellationRequested();
                ReportProgress(progress, 50, "Upscaling alpha mask...");
                ExecuteRealesrganWithProgress(maskPath, maskPath2, tile, token, progress, 50, 90, "Upscaling alpha mask");
                token.ThrowIfCancellationRequested();
                ReportProgress(progress, 92, "Compositing...");
                ExecuteMagickCommandCancellable($"\"{opaquePath2}\" \"{maskPath2}\" -alpha off -compose CopyOpacity -composite \"{finalPath}\"", token);
                ReportProgress(progress, 98, "Cleaning up...");
                if (!keepTemp)
                {
                    SafeDelete(maskPath);
                    SafeDelete(maskPath2);
                    SafeDelete(opaquePath);
                    SafeDelete(opaquePath2);
                }
                ReportProgress(progress, 100, "Done");
                return finalPath;
            }
            finally
            {
                // If canceled mid-way, don't leave tons of junk unless keepTemp is enabled.
                if (token.IsCancellationRequested && !checkTemp.Checked)
                {
                    SafeDelete(maskPath);
                    SafeDelete(maskPath2);
                    SafeDelete(opaquePath);
                    SafeDelete(opaquePath2);
                    // don't delete finalPath - it may be partial; let user decide.
                }
            }
        }
        private void SafeDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }
        private string UpscaleSingleFileToPng(string inputPath, CancellationToken token, IProgress<JobProgress> progress)
        {
            string ext = Path.GetExtension(inputPath).ToLowerInvariant();
            string workingPng = inputPath;
            if (ext == ".dds")
            {
                string pngPath = Path.ChangeExtension(inputPath, ".png");
                string outDir = Path.GetDirectoryName(pngPath);
                ReportProgress(progress, 0, "Converting DDS to PNG...");
                ConvertDDSToPNG_Cancellable(inputPath, outDir, pngPath, token);
                workingPng = pngPath;
            }
            return UpscaleSinglePng(workingPng, token, progress);
        }
        private void btnCancel_Click(object sender, EventArgs e)
        {
            if (_jobRunning)
            {
                btnCancel.Enabled = false;
                lblStatus.Text = "Canceling...";
                _cts?.Cancel();
            }
        }
        private async Task RunSingleUpscaleJobAsync()
        {
            if (string.IsNullOrEmpty(selectedImagePath))
            {
                Log("\nPlease select an image first.\n");
                return;
            }
            if (_jobRunning)
            {
                Log("\nA job is already running.\n");
                return;
            }
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var progress = new Progress<JobProgress>(p =>
            {
                if (progressBarJob != null)
                {
                    int v = Math.Max(0, Math.Min(100, p.Percent));
                    progressBarJob.Value = v;
                    if (lblPercent != null) lblPercent.Text = v.ToString() + "%";
                }
                if (lblStatus != null)
                {
                    lblStatus.Text = p.Status ?? "Working...";
                }
            });
            SetJobUiState(true, "Starting...");
            _suppressJobLog = true;
            try
            {
                string input = selectedImagePath;
                LogImportant("----------------------------------------------------------------------------------------");
                LogImportant($"START: Upscale {Path.GetFileName(input)}");
                string finalPath = await Task.Run(() => UpscaleSingleFileToPng(input, token, progress));
                // Update UI state after completion
                selectedImagePath = finalPath;
                LogImportant($"DONE: Upscale output {finalPath}");
                UpdateDimensionFieldsFast(finalPath);
                Texdiag(finalPath);
                LogData("-----------------------------------------");
                txtName.Text = Path.GetFileName(finalPath);
                txtName.SelectionStart = txtName.Text.Length;
                txtName.ScrollToCaret();
                PlayCompletionSound();
                btnUpscale.ForeColor = Color.Black;
                btnUpscale.BackColor = Color.Lime;
                BtnOpen2.ForeColor = Color.Black;
                BtnOpen2.BackColor = Color.Lime;
                BtnOpen3.ForeColor = Color.Black;
                BtnOpen3.BackColor = Color.Lime;
                Log("\nProcess complete!\n");
            }
            catch (OperationCanceledException)
            {
                LogImportant("Canceled.");
            }
            catch (Exception ex)
            {
                LogImportant("ERROR: " + ex.Message);
            }
            finally
            {
                _suppressJobLog = false;
                SetJobUiState(false, "Idle");
                try { _cts?.Dispose(); } catch { }
                _cts = null;
            }
        }
        private string UpscaleOneFileBatch(
            string inputPath,
            string outputRoot,
            string tempRoot,
            CancellationToken token,
            IProgress<JobProgress> progress,
            int index,
            int total)
        {
            string relativeSubPath = Path.GetDirectoryName(GetRelativePath(selectedFolderPath, inputPath));
            string outputDir = Path.Combine(outputRoot, relativeSubPath ?? string.Empty);
            Directory.CreateDirectory(outputDir);
            string baseName = Path.GetFileNameWithoutExtension(inputPath);
            string originalExt = Path.GetExtension(inputPath);
            // Unique per-file temp dir to avoid name collisions
            string workTemp = Path.Combine(tempRoot, baseName + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workTemp);
            try
            {
                string tempInput = inputPath;
                token.ThrowIfCancellationRequested();
                ReportProgress(progress, (int)((index - 1) * 100.0 / total), $"[{index}/{total}] Preparing {Path.GetFileName(inputPath)}");
                if (originalExt.Equals(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    tempInput = Path.Combine(workTemp, baseName + ".png");
                    ConvertDDSToPNG_Cancellable(inputPath, workTemp, tempInput, token);
                }
                string opaque = Path.Combine(workTemp, baseName + "_opaque.png");
                string opaqueUpscaled = Path.Combine(workTemp, baseName + "_opaque_upscaled.png");
                string mask = Path.Combine(workTemp, baseName + "_mask.png");
                string maskUpscaled = Path.Combine(workTemp, baseName + "_mask_upscaled.png");
                string finalOutput = Path.Combine(outputDir, baseName + ".png");
                ExecuteMagickCommandCancellable($"\"{tempInput}\" -alpha off \"{opaque}\"", token);
                int w = 0, h = 0;
                if (!TryGetPngDimensions(opaque, out w, out h))
                {
                    using (var img = Image.FromFile(opaque)) { w = img.Width; h = img.Height; }
                }
                int tile = GetRecommendedTileSize(w, h);
                // Map per-file progress into global progress bar
                int globalStart = (int)((index - 1) * 100.0 / total);
                int globalEnd = (int)(index * 100.0 / total);
                // Opaque upscale consumes 40% of the file's slice
                ExecuteRealesrganWithProgress(opaque, opaqueUpscaled, tile, token, progress,
                    globalStart + (int)((globalEnd - globalStart) * 0.05),
                    globalStart + (int)((globalEnd - globalStart) * 0.45),
                    $"[{index}/{total}] Upscaling opaque");
                ExecuteMagickCommandCancellable($"\"{tempInput}\" -alpha extract \"{mask}\"", token);
                ExecuteRealesrganWithProgress(mask, maskUpscaled, tile, token, progress,
                    globalStart + (int)((globalEnd - globalStart) * 0.50),
                    globalStart + (int)((globalEnd - globalStart) * 0.90),
                    $"[{index}/{total}] Upscaling alpha mask");
                ExecuteMagickCommandCancellable($"\"{opaqueUpscaled}\" \"{maskUpscaled}\" -alpha off -compose CopyOpacity -composite \"{finalOutput}\"", token);
                ReportProgress(progress, globalEnd, $"[{index}/{total}] Done: {Path.GetFileName(finalOutput)}");
                return finalOutput;
            }
            finally
            {
                if (!checkTemp.Checked)
                {
                    try { if (Directory.Exists(workTemp)) Directory.Delete(workTemp, true); } catch { }
                }
            }
        }
        private async Task RunBatchUpscaleJobAsync()
        {
            if (string.IsNullOrEmpty(selectedFolderPath))
            {
                Log("\nPlease select a folder first.\n");
                return;
            }
            if (_jobRunning)
            {
                Log("\nA job is already running.\n");
                return;
            }
            string outputRoot = Path.Combine(selectedFolderPath, "UPSCALED");
            if (!Directory.Exists(outputRoot)) Directory.CreateDirectory(outputRoot);
            if (!CheckOverwriteWarning(outputRoot))
            {
                Log("\nProcess cancelled by user.\n");
                return;
            }
            string tempFolder = Path.Combine(selectedFolderPath, "TempProcessing");
            if (!Directory.Exists(tempFolder)) Directory.CreateDirectory(tempFolder);
            // Build file list up-front so we can log a clean START line.
            var imageFiles = Directory.GetFiles(selectedFolderPath, "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    && !f.Contains(Path.Combine("UPSCALED", ""))
                    && !f.Contains(Path.Combine("TempProcessing", "")))
                .ToList();
            int total = imageFiles.Count;
            if (total == 0)
            {
                Log("\nNo PNG/DDS files found.\n");
                return;
            }
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var progress = new Progress<JobProgress>(p =>
            {
                if (progressBarJob != null)
                {
                    int v = Math.Max(0, Math.Min(100, p.Percent));
                    progressBarJob.Value = v;
                    if (lblPercent != null) lblPercent.Text = v.ToString() + "%";
                }
                if (lblStatus != null) lblStatus.Text = p.Status ?? "Working...";
            });
            SetJobUiState(true, "Starting batch...");
            _suppressJobLog = true;
            LogImportant("----------------------------------------------------------------------------------------");
            LogImportant($"START: Batch upscale ({total} files)");
            try
            {
                await Task.Run(() =>
                {
                    for (int i = 0; i < total; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        string input = imageFiles[i];
                        int idx = i + 1;
                        try
                        {
                            _ = UpscaleOneFileBatch(input, outputRoot, tempFolder, token, progress, idx, total);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            LogImportant($"ERROR: {Path.GetFileName(input)} - {ex.Message}");
                        }
                    }
                });
                LogImportant("DONE: Batch upscale complete");
                PlayCompletionSound();
                btnUpscale2.ForeColor = Color.Black;
                btnUpscale2.BackColor = Color.Lime;
                BtnOpen1.ForeColor = Color.Black;
                BtnOpen1.BackColor = Color.Lime;
            }
            catch (OperationCanceledException)
            {
                LogImportant("Canceled.");
            }
            catch (Exception ex)
            {
                LogImportant("ERROR: " + ex.Message);
            }
            finally
            {
                _suppressJobLog = false;
                SetJobUiState(false, "Idle");
                try { _cts?.Dispose(); } catch { }
                _cts = null;
                if (!checkTemp.Checked)
                {
                    try { if (Directory.Exists(tempFolder)) Directory.Delete(tempFolder, true); } catch { }
                }
            }
        }
        // ------------------------------
// Batch PNG -> DDS conversion (async, cancelable, no UI freeze)
// ------------------------------
private async Task RunBatchPngToDdsAsync(bool srgb)
{
    ResetActionButtonColors();
    if (string.IsNullOrWhiteSpace(selectedFolderPath) || !Directory.Exists(selectedFolderPath))
    {
        Log("\nPlease select a folder first.\n");
        return;
    }
    if (_jobRunning)
    {
        Log("\nA job is already running.\n");
        return;
    }
    // Prefer converting files inside <selected>\UPSCALED if it exists (typical batch pipeline),
    // otherwise convert PNGs in the selected folder.
    string root = selectedFolderPath;
    if (root.EndsWith(Path.DirectorySeparatorChar + "UPSCALED", StringComparison.OrdinalIgnoreCase) ||
        root.EndsWith("/UPSCALED", StringComparison.OrdinalIgnoreCase) ||
        root.EndsWith("\\UPSCALED", StringComparison.OrdinalIgnoreCase))
    {
        // selectedFolderPath already points at UPSCALED
    }
    else
    {
        string maybeUpscaled = Path.Combine(selectedFolderPath, "UPSCALED");
        if (Directory.Exists(maybeUpscaled)) root = maybeUpscaled;
    }
    var pngFiles = Directory.GetFiles(root, "*.png", SearchOption.AllDirectories)
        .Where(f =>
            !f.Contains(Path.Combine("TempProcessing", "")) &&
            !f.Contains("TempFit_") &&
            !f.EndsWith("_opaque.png", StringComparison.OrdinalIgnoreCase) &&
            !f.EndsWith("_mask.png", StringComparison.OrdinalIgnoreCase) &&
            !f.EndsWith("_opaque_fit.png", StringComparison.OrdinalIgnoreCase) &&
            !f.EndsWith("_mask_fit.png", StringComparison.OrdinalIgnoreCase))
        .ToList();
    int total = pngFiles.Count;
    if (total == 0)
    {
        Log("\nNo PNG files found to convert.\n");
        return;
    }
    _cts = new CancellationTokenSource();
    var token = _cts.Token;
    int deletedCount = 0;
    var progress = new Progress<JobProgress>(p =>
    {
        if (progressBarJob != null)
        {
            progressBarJob.Style = ProgressBarStyle.Blocks;
            int v = Math.Max(0, Math.Min(100, p.Percent));
            progressBarJob.Value = v;
            if (lblPercent != null) lblPercent.Text = v.ToString() + "%";
        }
        if (lblStatus != null) lblStatus.Text = p.Status ?? "Working...";
    });
    SetJobUiState(true, srgb ? "Batch converting to DDS (sRGB)..." : "Batch converting to DDS (Linear)...");
            await Task.Yield();
    _suppressJobLog = true;
    LogImportant("----------------------------------------------------------------------------------------");
    LogImportant($"START: Batch convert PNG -> DDS ({(srgb ? "sRGB" : "Linear")}) ({total} files)");
    try
    {
        await Task.Run(() =>
        {
            for (int i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();
                string png = pngFiles[i];
                int idx = i + 1;
                int percent = (int)(idx * 100.0 / total);
                ReportProgress(progress, percent, $"[{idx}/{total}] Converting {Path.GetFileName(png)}");
                try
                {
                    // Convert next to the PNG, preserving hidden RGB under alpha (Option B fit resize is already alpha-safe).
                    _ = ConvertPngToDdsCancellable(png, srgb, token);
                    // If "Save PNG Files" is unchecked, delete source PNG after successful conversion.
                    if (pngCheck != null && !pngCheck.Checked)
                    {
                        try { File.Delete(png); deletedCount++; } catch { }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    LogImportant($"ERROR: {Path.GetFileName(png)} - {ex.Message}");
                }
            }
        });
        if (pngCheck != null && !pngCheck.Checked && deletedCount > 0)
        {
            LogImportant($"INFO: Deleted {deletedCount} PNG file(s) after conversion (Save PNG Files unchecked).");
        }
        LogImportant("DONE: Batch DDS conversion complete");
        PlayCompletionSound();
        if (srgb)
        {
            btnDifuse3.ForeColor = Color.Black;
            btnDifuse3.BackColor = Color.Lime;
        }
        else
        {
            btnLinear3.ForeColor = Color.Black;
            btnLinear3.BackColor = Color.Lime;
        }
        BtnOpen1.ForeColor = Color.Black;
        BtnOpen1.BackColor = Color.Lime;
    }
    catch (OperationCanceledException)
    {
        LogImportant("Canceled.");
    }
    catch (Exception ex)
    {
        LogImportant("ERROR: " + ex.Message);
    }
    finally
    {
        _suppressJobLog = false;
        SetJobUiState(false, "Idle");
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        try
        {
            if (progressBarJob != null)
            {
                progressBarJob.Style = ProgressBarStyle.Blocks;
                progressBarJob.MarqueeAnimationSpeed = 0;
                progressBarJob.Value = 0;
            }
            if (lblPercent != null) lblPercent.Text = "";
        }
        catch { }
    }
}
private async Task RunResizeJobAsync(int newWidth, int newHeight)
        {
            if (string.IsNullOrEmpty(selectedImagePath))
            {
                Log("\nPlease select an image first.\n");
                return;
            }
            if (_jobRunning)
            {
                Log("\nA job is already running.\n");
                return;
            }
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var progress = new Progress<JobProgress>(p =>
            {
                if (progressBarJob != null)
                {
                    int v = Math.Max(0, Math.Min(100, p.Percent));
                    progressBarJob.Value = v;
                    if (lblPercent != null) lblPercent.Text = v.ToString() + "%";
                }
                if (lblStatus != null) lblStatus.Text = p.Status ?? "Working...";
            });
            SetJobUiState(true, "Starting resize...");
            _suppressJobLog = true;
            string input = selectedImagePath;
            string directory = Path.GetDirectoryName(input);
            string baseName = Path.GetFileNameWithoutExtension(input);
            string opaquePath = Path.Combine(directory, $"{baseName}_opaque_resized.png");
            string maskPath = Path.Combine(directory, $"{baseName}_mask_resized.png");
            string finalPath = Path.Combine(directory, $"{baseName}_resized.png");
            bool keepTemp = checkTemp.Checked;
            try
            {
                await Task.Run(() =>
                {
                    ReportProgress(progress, 5, "Resizing opaque...");
                    ExecuteMagickCommandCancellable($"\"{input}\" -alpha off -resize {newWidth}x{newHeight}\\! \"{opaquePath}\"", token);
                    ReportProgress(progress, 45, "Resizing alpha mask...");
                    ExecuteMagickCommandCancellable($"\"{input}\" -alpha extract -resize {newWidth}x{newHeight}\\! \"{maskPath}\"", token);
                    ReportProgress(progress, 85, "Compositing...");
                    ExecuteMagickCommandCancellable($"\"{opaquePath}\" \"{maskPath}\" -alpha off -compose CopyOpacity -composite \"{finalPath}\"", token);
                    if (!keepTemp)
                    {
                        SafeDelete(maskPath);
                        SafeDelete(opaquePath);
                    }
                    ReportProgress(progress, 100, "Done");
                });
                selectedImagePath = finalPath;
                Log($"\nSelected image: {finalPath}");
                UpdateDimensionFieldsFast(finalPath);
                Texdiag(finalPath);
                LogData("-----------------------------------------");
                PlayCompletionSound();
                btnResize.ForeColor = Color.Black;
                btnResize.BackColor = Color.Lime;
                BtnOpen2.ForeColor = Color.Black;
                BtnOpen2.BackColor = Color.Lime;
                BtnOpen3.ForeColor = Color.Black;
                BtnOpen3.BackColor = Color.Lime;
            }
            catch (OperationCanceledException)
            {
                Log("\nResize canceled.\n");
            }
            catch (Exception ex)
            {
                Log($"\nResize failed: {ex.Message}\n");
            }
            finally
            {
                _suppressJobLog = false;
                SetJobUiState(false, "Idle");
                try { _cts?.Dispose(); } catch { }
                _cts = null;
            }
        }
private void pngCheck_CheckedChanged(object sender, EventArgs e)
        {
        }
    }
}