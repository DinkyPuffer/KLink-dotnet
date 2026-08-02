using System.Windows;

namespace KLink.App.Views;

/// <summary>
/// 添加/更新弹窗（复刻脚本的 Toplevel 表单）：卡牌与卡组两种模式共用，
/// 确认后通过 <see cref="Result"/> 返回输入结果。
/// </summary>
public partial class CardIdEditDialog : Window
{
    public enum Mode { Card, Deck }

    /// <summary>输入结果：第一项文本、数字 ID、第三项文本（卡牌模式为 count 字符串，卡组模式为 card 名）。</summary>
    public (string Text1, int Id, string Text3)? Result { get; private set; }

    private readonly Mode _mode;

    public CardIdEditDialog(Mode mode, string? preset1 = null, string? presetId = null, string? preset3 = null)
    {
        InitializeComponent();
        _mode = mode;

        if (mode == Mode.Card)
        {
            Title = "添加/更新卡牌（library.json）";
            TxtTitle.Text = "添加/更新卡牌";
            Lbl1.Text = "卡牌资源名（card_type）";
            Lbl2.Text = "数字 ID（id）";
            Lbl3.Text = "拥有数量（count）";
            Input3.Text = "4";
        }
        else
        {
            Title = "添加/更新卡组代码（deck_code_ids.json）";
            TxtTitle.Text = "添加/更新卡组代码";
            Lbl1.Text = "卡组代码（对象键）";
            Lbl2.Text = "数字 ID（ID）";
            Lbl3.Text = "对应卡牌（card）";
        }

        if (preset1 is not null) Input1.Text = preset1;
        if (presetId is not null) Input2.Text = presetId;
        if (preset3 is not null) Input3.Text = preset3;

        Loaded += (_, _) => { Input1.Focus(); Input1.SelectAll(); };
        Input1.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Ok_Click(this, new RoutedEventArgs()); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var text1 = Input1.Text.Trim();
        if (text1.Length == 0)
        {
            ShowError(_mode == Mode.Card ? "卡牌资源名不能为空" : "卡组代码不能为空");
            return;
        }
        if (!int.TryParse(Input2.Text.Trim(), out var id))
        {
            ShowError("数字 ID 必须是整数");
            return;
        }
        var text3 = Input3.Text.Trim();
        if (_mode == Mode.Card)
        {
            if (!int.TryParse(text3.Length == 0 ? "4" : text3, out var count) || count < 0)
            {
                ShowError("拥有数量必须是整数");
                return;
            }
            text3 = count.ToString();
        }
        else if (text3.Length == 0)
        {
            ShowError("对应卡牌不能为空");
            return;
        }

        Result = (text1, id, text3);
        DialogResult = true;
    }

    private async void ShowError(string message)
        => await new Wpf.Ui.Controls.MessageBox
        {
            Title = "输入错误",
            Content = message,
        }.ShowDialogAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
