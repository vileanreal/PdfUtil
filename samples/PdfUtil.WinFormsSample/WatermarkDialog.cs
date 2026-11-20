using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PdfUtil.WinFormsSample;

/// <summary>
/// Watermark settings, like the web sample's Watermark page: text (with an optional TrueType font, size and colour)
/// or an image, then opacity, rotation, position, pages and layer, with a preview on an A4 page.
/// </summary>
internal sealed class WatermarkDialog : Form
{
    private const double A4Width = 595;
    private const double A4Height = 842;

    private readonly TabControl _kind = new() { Dock = DockStyle.Top, Height = 190 };
    private readonly TextBox _text = new() { Multiline = true, AcceptsReturn = true, Height = 46, Width = 330, Text = "CONFIDENTIAL", ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _fontPath = new() { Width = 180, ReadOnly = true };
    private readonly TrackBar _fontSize = new() { Minimum = 8, Maximum = 144, Value = 48, TickFrequency = 8, Width = 250 };
    private readonly Label _fontSizeLabel = new() { AutoSize = true };
    private readonly Button _color = new() { Width = 80, Text = "#808080", BackColor = Color.Gray, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
    private readonly TextBox _imagePath = new() { Width = 250, ReadOnly = true };
    private readonly CheckBox _ownSize = new() { Text = "The image's own size", AutoSize = true, Checked = true };
    private readonly NumericUpDown _imageWidth = new() { Minimum = 10, Maximum = 2000, Value = 150, Width = 80, Enabled = false };
    private readonly TrackBar _opacity = new() { Minimum = 1, Maximum = 20, Value = 6, Width = 250 };
    private readonly Label _opacityLabel = new() { AutoSize = true };
    private readonly TrackBar _rotation = new() { Minimum = -36, Maximum = 36, Value = 9, TickFrequency = 6, Width = 250 };
    private readonly Label _rotationLabel = new() { AutoSize = true };
    private readonly ComboBox _position = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox _pages = new() { Width = 160 };
    private readonly CheckBox _under = new() { Text = "Behind the page content", AutoSize = true };
    private readonly PreviewPanel _preview = new() { Width = 238, Height = 337, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.White };

    private byte[]? _fontData;
    private byte[]? _imageData;
    private Image? _image;
    private FontFamily? _fontFamily;
    private PrivateFontCollection? _fonts;

    public WatermarkDialog()
    {
        Text = "Watermark";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 470);

        foreach (var p in Enum.GetValues(typeof(PdfStampPosition)).Cast<PdfStampPosition>().Where(p => p != PdfStampPosition.Custom))
        {
            _position.Items.Add(new PositionItem(p));
        }

        _position.SelectedIndex = 0;

        _kind.TabPages.Add(TextPage());
        _kind.TabPages.Add(ImagePage());

        var common = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(4, 8, 4, 4) };
        common.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        common.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(common, "Opacity", Row(_opacity, _opacityLabel));
        AddRow(common, "Rotation", Row(_rotation, _rotationLabel));
        AddRow(common, "Position", _position);
        AddRow(common, "Pages", Row(_pages, new Label { Text = "e.g. 1-3, 5, last (empty: all)", AutoSize = true, ForeColor = SystemColors.GrayText, Padding = new Padding(0, 4, 0, 0) }));
        AddRow(common, "", _under);

        var ok = new Button { Text = "Add watermark", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => Accept();
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(4) };
        buttons.Controls.AddRange(new Control[] { cancel, ok });
        AcceptButton = ok;
        CancelButton = cancel;

        var settings = new Panel { Dock = DockStyle.Fill };
        settings.Controls.Add(common);
        settings.Controls.Add(_kind);

        var previewBox = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 260, FlowDirection = FlowDirection.TopDown, Padding = new Padding(8) };
        previewBox.Controls.Add(new Label { Text = "Preview (an A4 page)", AutoSize = true, ForeColor = SystemColors.GrayText });
        previewBox.Controls.Add(_preview);

        Controls.Add(settings);
        Controls.Add(previewBox);
        Controls.Add(buttons);

        _preview.Paint += (_, e) => PaintPreview(e.Graphics);
        foreach (var c in new Control[] { _text, _fontSize, _opacity, _rotation, _position, _under, _ownSize, _imageWidth, _kind })
        {
            switch (c)
            {
                case TrackBar t: t.ValueChanged += (_, _) => Changed(); break;
                case ComboBox b: b.SelectedIndexChanged += (_, _) => Changed(); break;
                case CheckBox x: x.CheckedChanged += (_, _) => Changed(); break;
                case NumericUpDown n: n.ValueChanged += (_, _) => Changed(); break;
                case TabControl k: k.SelectedIndexChanged += (_, _) => Changed(); break;
                default: c.TextChanged += (_, _) => Changed(); break;
            }
        }

        Changed();
    }

    private bool UseImage => _kind.SelectedIndex == 1;

    private double Opacity01 => _opacity.Value * 0.05;

    private int RotationDegrees => _rotation.Value * 5;

    private PdfStampPosition Position => ((PositionItem)_position.SelectedItem).Value;

    /// <summary>The stamp options for the current settings (without a password).</summary>
    public PdfStampOptions Options() => new()
    {
        Text = UseImage ? null : _text.Text,
        Image = UseImage ? _imageData : null,
        ImageWidth = UseImage && !_ownSize.Checked ? (double)_imageWidth.Value : null,
        FontData = UseImage ? null : _fontData,
        FontSize = _fontSize.Value,
        Color = PdfColor.FromHex(_color.Text),
        Opacity = Opacity01,
        Rotation = RotationDegrees,
        Position = Position,
        Layer = _under.Checked ? PdfStampLayer.Under : PdfStampLayer.Over,
        Pages = string.IsNullOrWhiteSpace(_pages.Text) ? null : PdfPageRange.Parse(_pages.Text),
    };

    /// <summary>A short description of the settings, for the result text.</summary>
    public string Summary() =>
        (UseImage ? $"image {Path.GetFileName(_imagePath.Text)}" : $"\"{_text.Text.Replace(Environment.NewLine, " / ")}\" {_fontSize.Value} pt {_color.Text}")
        + $", opacity {Opacity01:P0}, rotation {RotationDegrees}°, {(PositionItem)_position.SelectedItem}"
        + (string.IsNullOrWhiteSpace(_pages.Text) ? "" : $", pages {_pages.Text}")
        + (_under.Checked ? ", behind the content" : "");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _image?.Dispose();
            _fonts?.Dispose();
        }

        base.Dispose(disposing);
    }

    private TabPage TextPage()
    {
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => PickFont();
        var clear = new Button { Text = "Clear", AutoSize = true };
        clear.Click += (_, _) => SetFont(null, null);
        _color.Click += (_, _) => PickColor();

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(4) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(table, "Text", _text);
        AddRow(table, "Font", Row(_fontPath, browse, clear));
        AddRow(table, "", new Label { Text = "Optional TrueType font, for any script. Without one, Helvetica is used (Western European text).", AutoSize = true, MaximumSize = new Size(340, 0), ForeColor = SystemColors.GrayText });
        AddRow(table, "Font size", Row(_fontSize, _fontSizeLabel));
        AddRow(table, "Colour", _color);
        var page = new TabPage("Text");
        page.Controls.Add(table);
        return page;
    }

    private TabPage ImagePage()
    {
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => PickImage();
        _ownSize.CheckedChanged += (_, _) => _imageWidth.Enabled = !_ownSize.Checked;

        var table = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(4) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        AddRow(table, "Image", Row(_imagePath, browse));
        AddRow(table, "", new Label { Text = "A PNG or JPEG image.", AutoSize = true, ForeColor = SystemColors.GrayText });
        AddRow(table, "Width", Row(_ownSize, _imageWidth, new Label { Text = "points", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }));
        var page = new TabPage("Image");
        page.Controls.Add(table);
        return page;
    }

    private static void AddRow(TableLayoutPanel table, string caption, Control control)
    {
        table.RowCount++;
        table.Controls.Add(new Label { Text = caption, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, table.RowCount - 1);
        table.Controls.Add(control, 1, table.RowCount - 1);
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        row.Controls.AddRange(controls);
        return row;
    }

    private void Changed()
    {
        _fontSizeLabel.Text = $"{_fontSize.Value} pt";
        _opacityLabel.Text = $"{Opacity01:P0}";
        _rotationLabel.Text = $"{RotationDegrees}°";
        _preview.Invalidate();
    }

    /// <summary>Closes with OK if the settings are complete; otherwise says what is missing.</summary>
    private void Accept()
    {
        string? problem = null;
        if (UseImage ? _imageData is null : string.IsNullOrWhiteSpace(_text.Text))
        {
            problem = UseImage ? "Choose an image." : "Enter the watermark text.";
        }
        else if (!string.IsNullOrWhiteSpace(_pages.Text) && !PdfPageRange.TryParse(_pages.Text, out _, out var error))
        {
            problem = "Pages: " + error;
        }

        if (problem is not null)
        {
            MessageBox.Show(this, problem, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
    }

    private void PickFont()
    {
        using var dialog = new OpenFileDialog { Filter = "TrueType fonts (*.ttf;*.ttc)|*.ttf;*.ttc" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetFont(dialog.FileName, File.ReadAllBytes(dialog.FileName));
    }

    private void SetFont(string? path, byte[]? data)
    {
        _fontPath.Text = path ?? "";
        _fontData = data;
        _fonts?.Dispose();
        _fonts = null;
        _fontFamily = null;
        if (path is not null)
        {
            try
            {
                _fonts = new PrivateFontCollection();
                _fonts.AddFontFile(path);
                _fontFamily = _fonts.Families.FirstOrDefault();
            }
            catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or ExternalException)
            {
                // The preview falls back to Arial; PdfUtil reports a font it cannot use when stamping.
            }
        }

        _preview.Invalidate();
    }

    private void PickColor()
    {
        using var dialog = new ColorDialog { Color = _color.BackColor, FullOpen = true };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var c = dialog.Color;
        _color.BackColor = c;
        _color.ForeColor = (c.R * 299 + c.G * 587 + c.B * 114) / 1000 > 128 ? Color.Black : Color.White;
        _color.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _preview.Invalidate();
    }

    private void PickImage()
    {
        using var dialog = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var bytes = File.ReadAllBytes(dialog.FileName);
        Image image;
        try
        {
            image = Image.FromStream(new MemoryStream(bytes));
        }
        catch (ArgumentException)
        {
            MessageBox.Show(this, "The file is not a PNG or JPEG image.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _image?.Dispose();
        _image = image;
        _imageData = bytes;
        _imagePath.Text = dialog.FileName;
        _imageWidth.Value = Math.Max(_imageWidth.Minimum, Math.Min(_imageWidth.Maximum, image.Width));
        _preview.Invalidate();
    }

    /// <summary>Draws an A4 page with grey lines for text and the watermark, scaled from points to the panel.</summary>
    private void PaintPreview(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        var scale = (float)(_preview.ClientSize.Width / A4Width);
        if (_under.Checked)
        {
            PaintWatermark(g, scale);
            PaintLines(g);
        }
        else
        {
            PaintLines(g);
            PaintWatermark(g, scale);
        }
    }

    private void PaintLines(Graphics g)
    {
        var w = _preview.ClientSize.Width;
        var h = _preview.ClientSize.Height;
        using var brush = new SolidBrush(Color.FromArgb(233, 236, 239));
        for (var i = 0; i < 26; i++)
        {
            var y = (h * 0.08f) + (i * h * 0.033f);
            g.FillRectangle(brush, w * 0.09f, y, w * 0.82f * (70 + ((i * 37) % 30)) / 100f, h * 0.013f);
        }
    }

    private void PaintWatermark(Graphics g, float scale)
    {
        SizeF size;
        Action<Graphics, PointF> draw;
        if (UseImage)
        {
            if (_image is null)
            {
                return;
            }

            var width = (float)(_ownSize.Checked ? _image.Width : (double)_imageWidth.Value) * scale;
            size = new SizeF(width, width * _image.Height / _image.Width);
            var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = (float)Opacity01 });
            var image = _image;
            draw = (gr, at) => gr.DrawImage(image, new Rectangle((int)at.X, (int)at.Y, (int)size.Width, (int)size.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        }
        else
        {
            if (_text.Text.Length == 0)
            {
                return;
            }

            var font = new Font(_fontFamily ?? new FontFamily("Arial"), Math.Max(1, _fontSize.Value * scale), GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center };
            size = g.MeasureString(_text.Text, font, PointF.Empty, format);
            var color = Color.FromArgb((int)(Opacity01 * 255), _color.BackColor);
            var text = _text.Text;
            draw = (gr, at) =>
            {
                using var brush = new SolidBrush(color);
                using var f = new StringFormat { Alignment = StringAlignment.Center };
                gr.DrawString(text, font, brush, new RectangleF(at, size), f);
            };
        }

        // Anchor as the PDF stamp does: corners and edges sit a margin in, the rest is centred; then rotate
        // counter-clockwise (screen y points down, so the angle is negated) around the watermark's centre.
        var name = Position.ToString();
        var margin = (float)(36 * scale);
        var pw = _preview.ClientSize.Width;
        var ph = _preview.ClientSize.Height;
        var x = name.EndsWith("Left", StringComparison.Ordinal) ? margin : name.EndsWith("Right", StringComparison.Ordinal) ? pw - margin - size.Width : (pw - size.Width) / 2;
        var y = name.StartsWith("Top", StringComparison.Ordinal) ? margin : name.StartsWith("Bottom", StringComparison.Ordinal) ? ph - margin - size.Height : (ph - size.Height) / 2;
        var state = g.Save();
        g.TranslateTransform(x + (size.Width / 2), y + (size.Height / 2));
        g.RotateTransform(-RotationDegrees);
        draw(g, new PointF(-size.Width / 2, -size.Height / 2));
        g.Restore(state);
    }

    private sealed class PositionItem
    {
        public PositionItem(PdfStampPosition value) => Value = value;

        public PdfStampPosition Value { get; }

        public override string ToString()
        {
            // "TopLeft" -> "Top left", "Center" -> "Centre".
            var words = string.Concat(Value.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
            return words.Replace("Center", "Centre").Replace("center", "centre");
        }
    }

    /// <summary>A panel that paints without flicker.</summary>
    private sealed class PreviewPanel : Panel
    {
        public PreviewPanel() => DoubleBuffered = true;
    }
}
