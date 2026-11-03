namespace NodeRunner.App;

// The control DirectX draws into. Windows Forms must not paint it itself (no flicker), and arrow keys / Tab must
// reach ImGui instead of moving the focus between controls.
internal sealed class RenderSurface : Control
{
    public RenderSurface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.UserPaint, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        TabStop = true;
    }

    protected override bool IsInputKey(Keys keyData) => true;

    // True for the points that belong to the window frame drawn by the interface (title bar, top edge): the mouse
    // there goes to the main form, which tells Windows to drag, resize or show the snap layouts.
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<Point, bool>? IsWindowFrame { get; set; }

    private const int WmNcHitTest = 0x84;
    private const int HtTransparent = -1;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest && IsWindowFrame is { } isFrame)
        {
            var screen = new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)));
            if (isFrame(screen))
            {
                m.Result = HtTransparent;
                return;
            }
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
    }
}
