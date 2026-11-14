using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DesktopZones.Views.Components;

/// <summary>
/// ponytail 2026-11-14(性能审计): 列表页刷新时用的 ObservableCollection。
///
/// 各管理页的 <c>RefreshList()</c> 原来是 <c>_rows.Clear(); foreach (...) _rows.Add(...)</c>
/// —— <c>Clear + N 次 Add</c> 会给 ItemsControl 发 1 次 Reset + N 次 Add 通知，
/// 每一行都触发一次布局/生成容器。列表越长、刷新越频繁(每次 ZonesChanged/ClocksChanged
/// 都会重建)，浪费越明显。
///
/// <see cref="ReplaceAll"/> 直接操作底层 <see cref="Collection{T}.Items"/>，最后只发
/// 一次 Reset 通知 —— UI 只重建一轮。语义等价于「清空后按顺序重填」。
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>用 <paramref name="items"/> 整体替换当前内容，只发一次 Reset 通知。</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        // 先物化，允许调用方传 source 本身就是本集合的惰性投影（防御性）。
        var buffer = items as IList<T> ?? new List<T>(items);

        // 内容没变就完全不动 UI（页面上常见：刷新时数据其实一样）。
        if (buffer.Count == Count)
        {
            bool same = true;
            for (int i = 0; i < buffer.Count; i++)
            {
                if (!EqualityComparer<T>.Default.Equals(this[i], buffer[i])) { same = false; break; }
            }
            if (same) return;
        }

        CheckReentrancy();
        Items.Clear();
        foreach (var item in buffer)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
