using System;
using System.Collections.Generic;

// Shared by the hook, menu, cycle event and --plan. Indices: UA=0, RU=1, EN=2.
static class LayoutPlan
{
    public const int CtrlShift = 1, AltShift = 2, Cycle = 3;
    public static int Target(int current, int lastLatin, int action)
    {
        if (lastLatin != 1 && lastLatin != 2) lastLatin = 2;
        if (current < 0 || current > 2) current = lastLatin;
        if (action == Cycle) return (current + 1) % 3;
        if (action == AltShift) return current == 0 ? lastLatin : 0;
        return current == 0 ? lastLatin : current == 1 ? 2 : 1;
    }
}

// Real key transitions only; repeats cannot arm a second switch. A dirty gesture stays dirty
// until every key is released, including a non-modifier held before the chord began.
sealed class PureChord
{
    public readonly HashSet<int> Keys = new HashSet<int>();
    bool dirty, fired;
    static bool Shift(int k) { return k == 0xA0 || k == 0xA1; }
    static bool Ctrl(int k) { return k == 0xA2 || k == 0xA3; }
    public void Interrupt() { if (Keys.Count > 0) dirty = true; }
    public void Reset() { Keys.Clear(); dirty = fired = false; }
    public void Reconcile(Func<int, bool> isDown)
    {
        if (Keys.RemoveWhere(k => !isDown(k)) > 0) dirty = true;
        if (Keys.Count == 0) dirty = fired = false;
    }
    public int Key(int vk, bool down, bool mouseDown)
    {
        int action = 0;
        if (down)
        {
            if (!Keys.Add(vk)) return 0;
            if (mouseDown || Keys.Count > 2 || !(Shift(vk) || Ctrl(vk) || vk == 0xA4)) dirty = true;
        }
        else
        {
            if (Keys.Contains(vk) && Keys.Count == 2 && !dirty && !fired)
            {
                bool shift = false, ctrl = false;
                foreach (int k in Keys) { shift |= Shift(k); ctrl |= Ctrl(k); }
                if (shift && Keys.Contains(0xA4)) action = LayoutPlan.AltShift;
                else if (shift && ctrl) action = LayoutPlan.CtrlShift;
                if (action != 0) fired = true;
            }
            Keys.Remove(vk);
            if (Keys.Count == 0) dirty = fired = false;
        }
        return action;
    }
}
