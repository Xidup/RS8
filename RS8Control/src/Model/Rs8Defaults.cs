namespace RS8Control.Model;

internal static class Rs8Defaults
{
    /// <summary>
    /// 离线底图：87 键 TKL ANSI 的物理键名，按矩阵坐标（6 行 × 16 列）排列，空位为 ""。
    /// 连上键盘时不用这张表 —— 键名一律取自键盘第 0 层。
    /// </summary>
    public static readonly string[] FallbackPhysical =
    {
        "Esc", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "PRINT", "SCRLK", "PAUSE",
        "`~", "1!", "2@", "3#", "4$", "5%", "6^", "7&", "8*", "9(", "0)", "-_", "=+", "Backspace", "INS", "HOME",
        "Tab", "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[{", "]}", "\\|", "DEL", "END",
        "CapsLock", "A", "S", "D", "F", "G", "H", "J", "K", "L", ";:", "'\"", "", "Enter", "PGUP", "PGDN",
        "左 Shift", "", "Z", "X", "C", "V", "B", "N", "M", ",<", ".>", "/?", "", "右 Shift", "↑", "",
        "左 Ctrl", "左 Win", "左 Alt", "", "", "Space", "", "", "", "右 Alt", "Fn", "菜单键", "右 Ctrl", "←", "↓", "→",
    };
}
