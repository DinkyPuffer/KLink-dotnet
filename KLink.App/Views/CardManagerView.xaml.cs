using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using KLink.App.Services;
using Microsoft.Win32;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;

namespace KLink.App.Views;

/// <summary>卡牌表格行（绑定用）。</summary>
public sealed class CardEntryView
{
    public int Id { get; set; }
    public string CardType { get; set; } = "";
    public int Count { get; set; }
}

/// <summary>卡组代码表格行（绑定用）。</summary>
public sealed class DeckEntryView
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Card { get; set; } = "";
}

/// <summary>
/// 卡牌/卡组 ID 管理器（复刻需求/klink_card_id_manager.py 的 GUI 功能）：
/// 编辑 library.json 与 deck_code_ids.json，保存到 exe 同级 data\kards-server 覆盖文件，重启服务器后生效。
/// </summary>
public partial class CardManagerView : UserControl
{
    private JsonNode _library = null!;   // {"cards":[...]}
    private JsonArray _cards = null!;    // _library["cards"]
    private JsonObject _decks = null!;   // {代码:{card,deck_code_id,ID}}

    public CardManagerView()
    {
        InitializeComponent();
        TxtLibraryPath.Text = CardIdManagerService.DefaultLibraryPath;
        TxtDeckCodesPath.Text = CardIdManagerService.DefaultDeckCodesPath;
        TxtCardSearch.TextChanged += (_, _) => RefreshCards();
        TxtDeckSearch.TextChanged += (_, _) => RefreshDecks();
        Loaded += (_, _) => Reload();
    }

    // ==================== 加载 / 刷新 ====================

    private void Reload()
    {
        try
        {
            _library = CardIdManagerService.LoadLibrary(TxtLibraryPath.Text);
            _decks = CardIdManagerService.LoadDeckCodes(TxtDeckCodesPath.Text) as JsonObject
                ?? throw new InvalidOperationException("deck_code_ids.json 顶层必须是对象");
            _cards = _library["cards"] as JsonArray
                ?? throw new InvalidOperationException("library.json 缺少 cards 数组");
            RefreshCards();
            RefreshDecks();
            Log($"已加载：卡牌 {_cards.Count}，卡组代码 {_decks.Count}（仅预览，未写入）");
            Log(CardIdManagerService.DescribeSource(TxtLibraryPath.Text));
        }
        catch (Exception ex)
        {
            ShowError("读取失败", ex.Message);
        }
    }

    private void RefreshCards()
    {
        if (CardList is null || _cards is null) return;
        var q = TxtCardSearch.Text.Trim().ToLowerInvariant();
        var rows = _cards
            .Select(c => new CardEntryView
            {
                Id = GetInt(c?["id"]),
                CardType = c?["card_type"]?.ToString() ?? "",
                Count = GetInt(c?["count"]),
            })
            .Where(x => q.Length == 0
                || x.CardType.ToLowerInvariant().Contains(q)
                || x.Id.ToString().Contains(q))
            .ToList();
        CardList.ItemsSource = rows;
        CardCountText.Text = $"共 {rows.Count} 项";
    }

    private void RefreshDecks()
    {
        if (DeckList is null || _decks is null) return;
        var q = TxtDeckSearch.Text.Trim().ToLowerInvariant();
        var rows = _decks
            .Select(kv => new DeckEntryView
            {
                Code = kv.Key,
                Id = GetInt(kv.Value?["ID"]),
                Card = kv.Value?["card"]?.ToString() ?? "",
            })
            .Where(x => q.Length == 0
                || x.Code.ToLowerInvariant().Contains(q)
                || x.Card.ToLowerInvariant().Contains(q)
                || x.Id.ToString().Contains(q))
            .ToList();
        DeckList.ItemsSource = rows;
        DeckCountText.Text = $"共 {rows.Count} 项";
    }

    private static int GetInt(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    // ==================== 添加 / 更新 / 删除 ====================

    private async void EditCard_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureLoaded())
            return;
        var sel = CardList.SelectedItems.Cast<CardEntryView>().FirstOrDefault();
        var dialog = new CardIdEditDialog(CardIdEditDialog.Mode.Card,
            sel?.CardType, sel?.Id.ToString(), sel?.Count.ToString())
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || dialog.Result is not { } r)
            return;

        // 去掉同 card_type 或同 id 的旧项后追加新项（与脚本一致）
        var stale = _cards
            .Where(c => c?["card_type"]?.ToString() == r.Text1 || GetInt(c?["id"]) == r.Id)
            .ToList();
        foreach (var c in stale)
            _cards.Remove(c);

        _cards.Add(new JsonObject
        {
            ["card_type"] = r.Text1,
            ["count"] = int.Parse(r.Text3),
            ["gold_card_count"] = 0,
            ["id"] = r.Id,
            ["recently_crafted_count"] = 0,
        });
        RefreshCards();
        Log("卡牌修改已预览，点击「保存全部修改」后写入");
    }

    private async void EditDeck_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureLoaded())
            return;
        var sel = DeckList.SelectedItems.Cast<DeckEntryView>().FirstOrDefault();
        var dialog = new CardIdEditDialog(CardIdEditDialog.Mode.Deck,
            sel?.Code, sel?.Id.ToString(), sel?.Card)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() != true || dialog.Result is not { } r)
            return;

        _decks[r.Text1] = new JsonObject
        {
            ["card"] = r.Text3,
            ["deck_code_id"] = r.Text1,
            ["ID"] = r.Id,
        };
        RefreshDecks();
        Log("卡组代码修改已预览，点击「保存全部修改」后写入");
    }

    private async void DeleteCards_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureLoaded())
            return;
        var selected = CardList.SelectedItems.Cast<CardEntryView>().ToList();
        if (selected.Count == 0)
            return;
        if (!await Confirm($"确定删除选中的 {selected.Count} 项吗？"))
            return;

        var ids = selected.Select(x => x.Id).ToHashSet();
        var stale = _cards.Where(c => ids.Contains(GetInt(c?["id"]))).ToList();
        foreach (var c in stale)
            _cards.Remove(c);
        RefreshCards();
        Log("已删除内存数据；点击「保存全部修改」后才会写入文件");
    }

    private async void DeleteDecks_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureLoaded())
            return;
        var selected = DeckList.SelectedItems.Cast<DeckEntryView>().ToList();
        if (selected.Count == 0)
            return;
        if (!await Confirm($"确定删除选中的 {selected.Count} 项吗？"))
            return;

        foreach (var x in selected)
            _decks.Remove(x.Code);
        RefreshDecks();
        Log("已删除内存数据；点击「保存全部修改」后才会写入文件");
    }

    // ==================== 检查 / 保存 ====================

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureLoaded())
            return;
        try
        {
            var errors = CardIdManagerService.Validate(_library, _decks);
            if (errors.Count == 0)
            {
                Log("一致性检查通过");
                await new WpfUiMessageBox { Title = "KLink-dotnet", Content = "一致性检查通过" }.ShowDialogAsync();
            }
            else
            {
                Log($"发现 {errors.Count} 个问题（部分为数据本身的历史遗留，不影响运行）");
                await new WpfUiMessageBox
                {
                    Title = "检查结果",
                    Content = $"发现 {errors.Count} 个问题：\n\n{string.Join("\n", errors.Take(30))}" + (errors.Count > 30 ? $"\n…另有 {errors.Count - 30} 个" : ""),
                }.ShowDialogAsync();
            }
        }
        catch (Exception ex)
        {
            await ShowError("检查失败", ex.Message);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureLoaded())
            return;
        try
        {
            string? bak1 = CardIdManagerService.Save(TxtLibraryPath.Text, _library);
            string? bak2 = CardIdManagerService.Save(TxtDeckCodesPath.Text, _decks);
            var backups = new[] { bak1, bak2 }.Where(b => b is not null).ToList();
            Log($"已保存：{TxtLibraryPath.Text}");
            Log($"已保存：{TxtDeckCodesPath.Text}");
            if (backups.Count > 0)
                Log("备份：" + string.Join("、", backups));
            await new WpfUiMessageBox
            {
                Title = "KLink-dotnet",
                Content = "已保存并写入覆盖文件，重启服务器后生效。",
            }.ShowDialogAsync();
        }
        catch (Exception ex)
        {
            await ShowError("保存失败", ex.Message);
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Reload();

    /// <summary>数据未加载（初次加载失败等）时提示并阻止后续操作。</summary>
    private async Task<bool> EnsureLoaded()
    {
        if (_cards is not null && _decks is not null)
            return true;
        await new WpfUiMessageBox
        {
            Title = "KLink-dotnet",
            Content = "数据未加载，请先点击「重新加载 / 预览」",
        }.ShowDialogAsync();
        return false;
    }

    // ==================== 浏览路径 ====================

    private void BrowseLibrary_Click(object sender, RoutedEventArgs e)
        => Browse(TxtLibraryPath);

    private void BrowseDeckCodes_Click(object sender, RoutedEventArgs e)
        => Browse(TxtDeckCodesPath);

    private void Browse(TextBox textBox)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 JSON 文件",
            Filter = "JSON (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() == true)
            textBox.Text = dialog.FileName;
    }

    // ==================== 小工具 ====================

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
        LogBox.ScrollToEnd();
    }

    private static async Task<bool> Confirm(string message)
        => await new WpfUiMessageBox
        {
            Title = "确认删除",
            Content = message,
            PrimaryButtonText = "删除",
            SecondaryButtonText = "取消",
        }.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary;

    private static async Task ShowError(string title, string message)
        => await new WpfUiMessageBox { Title = title, Content = message }.ShowDialogAsync();
}
