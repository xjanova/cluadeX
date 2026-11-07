using System.IO;
using System.Text.RegularExpressions;

namespace SmallBench;

/// <summary>One bench task: what the model is asked, and how the answer or the changed files are checked.
/// <para>"answer" tasks are graded on the final answer text (code facts that can only be known by reading the code).
/// "edit" tasks are graded by running a hidden Python check in the work folder after the run — the model never sees it.</para>
/// <para><see cref="Reference"/> is a known-good fix (edit tasks) or a known-good answer (answer tasks), used by
/// <c>--selfcheck</c> to prove every check passes on a right result and fails on the untouched fixture.</para>
/// <para><see cref="Setup"/> plants the task's own bug in its copy. The fixture itself compiles and runs: a syntax
/// error left in shipping.py for every task sent models off fixing shipping.py in tasks about cart.py (first run).</para></summary>
internal sealed record BenchTask(string Id, string Kind, string Shape, string Prompt,
    Func<string, (bool Ok, string Why)> CheckAnswer = null,
    string CheckScript = null,
    Func<string, string> Reference = null,
    string WrongAnswer = null,
    Action<string> Setup = null);

internal static class Tasks
{
    static bool Has(string s, params string[] any) => any.Any(a => s.Contains(a, StringComparison.OrdinalIgnoreCase));

    /// <summary>The line of <c>def apply_discount</c> in the fixture, so the check follows the file.</summary>
    static int DiscountLine(string fixture) =>
        Array.FindIndex(File.ReadAllLines(Path.Combine(fixture, "shopkit", "pricing.py")), l => l.StartsWith("def apply_discount")) + 1;

    public static List<BenchTask> All(string fixture)
    {
        int line = DiscountLine(fixture);
        return new List<BenchTask>
        {
            new("T01", "answer", "find-symbol",
                "In which file and on which line is the function `apply_discount` defined?",
                a => (Has(a, "pricing.py") && Regex.IsMatch(a, $@"(?<!\d){line}(?!\d)"),
                      $"needs pricing.py and line {line}"),
                Reference: _ => $"`apply_discount` is defined in `shopkit/pricing.py` on line {line}.",
                WrongAnswer: "It is defined in shopkit/cart.py on line 20."),

            new("T02", "answer", "question",
                "What is the maximum stock level the Inventory allows, and what happens when a restock would go over it?",
                a => (Has(a, "500") && Has(a, "ValueError"), "needs 500 and ValueError"),
                Reference: _ => "MAX_STOCK is 500; restock raises ValueError when the new level would exceed it.",
                WrongAnswer: "The maximum is 1000 and the extra units are silently dropped."),

            new("T03", "answer", "question-th",
                "ฟังก์ชัน `log_audit` ถูกเรียกใช้กี่ที่ (ไม่นับตัวที่ประกาศฟังก์ชัน) อยู่ในไฟล์ไหนบ้าง",
                a => ((Regex.IsMatch(a, @"(?<!\d)3(?!\d)") || Has(a, "three", "สาม")) && Has(a, "cart.py") && Has(a, "inventory.py"),
                      "needs 3 calls in cart.py and inventory.py"),
                Reference: _ => "เรียก 3 ที่: shopkit/cart.py (add_item) 1 ที่ และ shopkit/inventory.py (restock, take) 2 ที่",
                WrongAnswer: "เรียก 5 ที่ ใน cart.py, pricing.py และ shipping.py"),

            new("T04", "edit", "fix-compile",
                "`python -m compileall shopkit` fails. Find the error and fix it.",
                CheckScript: """
                    import compileall, sys
                    ok = compileall.compile_dir('shopkit', quiet=1, force=True)
                    from shopkit.shipping import shipping_cost
                    assert ok, 'compileall failed'
                    assert shipping_cost(150) == 0 and shipping_cost(50) == 5 and shipping_cost(150, True) == 15, 'shipping_cost changed'
                    """,
                Setup: w => Patch(w, "shopkit/shipping.py", "express=False):\n", "express=False)\n"),
                Reference: w => Patch(w, "shopkit/shipping.py", "express=False)\n", "express=False):\n")),

            new("T05", "edit", "fix-bug",
                "`slugify('Hello World!')` should return `'hello-world'` but it returns `'hello world'`. Fix it.",
                CheckScript: """
                    from shopkit.utils import slugify
                    for src, want in [('Hello World!', 'hello-world'), ('  A   B ', 'a-b'), ('Shop-Kit 2', 'shop-kit-2')]:
                        got = slugify(src)
                        assert got == want, f'slugify({src!r}) = {got!r}, want {want!r}'
                    """,
                Reference: w => Patch(w, "shopkit/utils.py", "return re.sub(r\"\\s\", \" \", text)", "return re.sub(r\"[\\s-]+\", \"-\", text).strip(\"-\")")),

            new("T06", "edit", "fix-crash",
                "Calling `Cart.total()` crashes with an AttributeError. Find out why and fix it.",
                CheckScript: """
                    from shopkit.cart import Cart
                    c = Cart(discount_pct=10)
                    c.add_item('pen', 10.0, 2)
                    t = c.total()
                    assert abs(t - 19.26) < 0.001, f'total = {t}, want 19.26'
                    """,
                Reference: w => Patch(w, "shopkit/cart.py", "pricing.applyDiscount(", "pricing.apply_discount(")),

            new("T07", "edit", "add-method-th",
                "เพิ่มเมธอด `is_empty()` ให้คลาส `Cart` คืนค่า True เมื่อตะกร้าไม่มีสินค้า และ False เมื่อมีสินค้า",
                CheckScript: """
                    from shopkit.cart import Cart
                    c = Cart()
                    assert c.is_empty() is True, 'empty cart must be True'
                    c.add_item('pen', 1.0)
                    assert c.is_empty() is False, 'cart with an item must be False'
                    """,
                Reference: w => Append(w, "shopkit/cart.py", "\n    def is_empty(self):\n        return len(self.items) == 0\n")),

            new("T08", "edit", "add-function",
                "Add a function `format_price(cents)` to `shopkit/utils.py` that turns an integer number of cents into a string like `'$12.34'` (5 → `'$0.05'`).",
                CheckScript: """
                    from shopkit.utils import format_price, slugify
                    for cents, want in [(1234, '$12.34'), (5, '$0.05'), (100000, '$1000.00'), (0, '$0.00')]:
                        got = format_price(cents)
                        assert got == want, f'format_price({cents}) = {got!r}, want {want!r}'
                    assert slugify('x') == 'x'
                    """,
                Reference: w => Append(w, "shopkit/utils.py", "\n\ndef format_price(cents):\n    return f\"${cents // 100}.{cents % 100:02d}\"\n")),

            new("T09", "answer", "question",
                "Which function raises `OutOfStockError`, and under what condition?",
                a => (Regex.IsMatch(a, @"\btake\b", RegexOptions.IgnoreCase)
                      && Has(a, "more than", "exceed", "greater", ">", "not enough", "insufficient", "have", "available", "in stock"),
                      "needs Inventory.take and the qty > stock condition"),
                Reference: _ => "`Inventory.take` raises it when the quantity asked for is more than what is in stock (qty > have).",
                WrongAnswer: "`Inventory.restock` raises it when the stock goes over MAX_STOCK."),

            new("T10", "answer", "trap",
                "Does the `Cart` class have a `remove_item` method? If it does, what does it do?",
                a => (Has(a, "no ", "not ", "doesn't", "does not", "isn't", "there is no", "ไม่มี", "no.", "no,")
                      && !Regex.IsMatch(a, @"remove_item\s*(\(|method)?[^.\n]{0,40}\b(removes|deletes|takes out)\b", RegexOptions.IgnoreCase),
                      "must say it does not exist"),
                Reference: _ => "No — `Cart` (shopkit/cart.py) only has add_item, subtotal and total; there is no remove_item.",
                WrongAnswer: "Yes, `remove_item(name)` removes the item with that name from the cart."),

            new("T11", "edit", "add-feature",
                "Add a `remove_item(name)` method to `Cart` that removes every item with that name and records it with `log_audit` (like `add_item` does).",
                CheckScript: """
                    from shopkit.cart import Cart
                    from shopkit import audit
                    c = Cart()
                    c.add_item('pen', 1.0); c.add_item('cup', 2.0); c.add_item('pen', 1.0, 3)
                    before = len(audit.entries())
                    c.remove_item('pen')
                    names = [n for n, _, _ in c.items]
                    assert names == ['cup'], f'items left = {names}'
                    assert len(audit.entries()) > before, 'remove_item must call log_audit'
                    """,
                Reference: w => Append(w, "shopkit/cart.py",
                    "\n    def remove_item(self, name):\n        self.items = [i for i in self.items if i[0] != name]\n        log_audit(f\"cart.remove {name}\")\n")),
        };
    }

    internal static string Patch(string work, string rel, string find, string replace)
    {
        string p = Path.Combine(work, rel);
        string t = File.ReadAllText(p).Replace("\r\n", "\n");
        if (!t.Contains(find)) throw new InvalidOperationException($"reference patch: '{find}' not in {rel}");
        File.WriteAllText(p, t.Replace(find, replace));
        return rel;
    }

    static string Append(string work, string rel, string text)
    {
        string p = Path.Combine(work, rel);
        File.WriteAllText(p, File.ReadAllText(p).Replace("\r\n", "\n").TrimEnd('\n') + "\n" + text);
        return rel;
    }
}
