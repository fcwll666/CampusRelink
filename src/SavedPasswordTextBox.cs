using System;
using System.Windows.Forms;

// The marker represents an existing encrypted secret, never its value or length.
// Editing clears the entire marker, so it cannot become part of a new password.
sealed class SavedPasswordTextBox : TextBox
{
    internal const string SavedMarker = "***************";
    bool showingSavedPassword;
    bool updatingDisplay;

    internal SavedPasswordTextBox()
    {
        UseSystemPasswordChar = false;
        PasswordChar = '*';
        BorderStyle = BorderStyle.FixedSingle;
    }

    internal string EnteredPassword { get { return showingSavedPassword ? "" : Text; } }

    internal void ShowSavedPassword(bool saved)
    {
        updatingDisplay = true;
        try
        {
            showingSavedPassword = saved;
            Text = saved ? SavedMarker : "";
            ClearUndo();
        }
        finally { updatingDisplay = false; }
    }

    void BeginEditing()
    {
        if (showingSavedPassword) ShowSavedPassword(false);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        if (!updatingDisplay) showingSavedPassword = false;
        base.OnTextChanged(e);
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        if (showingSavedPassword) SelectAll();
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        if (!Char.IsControl(e.KeyChar) || e.KeyChar == '\b') BeginEditing();
        base.OnKeyPress(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Delete) BeginEditing();
        base.OnKeyDown(e);
    }

    protected override void WndProc(ref Message message)
    {
        // Includes keyboard shortcuts and the native right-click paste menu.
        if (message.Msg == 0x0302 || message.Msg == 0x0300 || message.Msg == 0x0303)
            BeginEditing(); // WM_PASTE, WM_CUT, WM_CLEAR
        base.WndProc(ref message);
    }
}
