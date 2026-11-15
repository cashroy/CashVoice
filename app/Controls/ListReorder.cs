using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CashVoice.Controls;

/// <summary>Drag items of a ListBox up and down to reorder its ObservableCollection.</summary>
public static class ListReorder
{
    const string Format = "CashVoice.ListItem";

    public static void Enable<T>(ListBox list, Func<ObservableCollection<T>> items, Action changed) where T : class
    {
        Point start = default;
        T? dragging = null;
        list.AllowDrop = true;

        list.PreviewMouseLeftButtonDown += (_, e) =>
        {
            dragging = null;
            if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) != null) return;   // Play buttons etc.
            if (ItemAt(list, e.OriginalSource) is T item) { dragging = item; start = e.GetPosition(list); }
        };
        list.PreviewMouseMove += (_, e) =>
        {
            if (dragging == null || e.LeftButton != MouseButtonState.Pressed) return;
            var d = e.GetPosition(list) - start;
            if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance &&
                Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance) return;
            var item = dragging; dragging = null;
            DragDrop.DoDragDrop(list, new DataObject(Format, item), DragDropEffects.Move);
        };
        list.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetData(Format) is T ? DragDropEffects.Move : DragDropEffects.None;
            if (e.Effects == DragDropEffects.Move) e.Handled = true;     // don't show the file-drop cue
        };
        list.Drop += (_, e) =>
        {
            if (e.Data.GetData(Format) is not T moved) return;
            e.Handled = true;
            var coll = items();
            int from = coll.IndexOf(moved);
            if (from < 0) return;
            int to = ItemAt(list, e.OriginalSource) is T target ? coll.IndexOf(target) : coll.Count - 1;
            if (to < 0 || to == from) return;
            coll.Move(from, to);
            list.SelectedItem = moved;
            changed();
        };
    }

    static object? ItemAt(ListBox list, object source)
    {
        var c = FindAncestor<ListBoxItem>(source as DependencyObject);
        return c == null ? null : list.ItemContainerGenerator.ItemFromContainer(c);
    }

    static TA? FindAncestor<TA>(DependencyObject? d) where TA : DependencyObject
    {
        while (d != null)
        {
            if (d is TA t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
