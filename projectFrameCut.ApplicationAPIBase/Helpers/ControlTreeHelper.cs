using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Layouts;

namespace projectFrameCut.ApplicationAPIBase.Helpers
{
    /// <summary>
    /// 控件树辅助类：绑定到一个根 View，通过 AutomationID 递归枚举其下的所有子控件，
    /// 并提供读取/修改控件值的统一 API。
    /// </summary>
    public class ControlTreeHelper
    {
        private View _root;

        /// <summary>
        /// 绑定到指定根控件，后续操作均基于此控件树。
        /// </summary>
        /// <param name="root">根 View（通常是一个 Layout）。</param>
        public ControlTreeHelper(View root)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
        }

        /// <summary>
        /// 当前绑定的根控件。
        /// </summary>
        public View Root => _root;

        #region 递归枚举（static，对任意 View 操作）

        /// <summary>
        /// 获取指定控件的所有直接可视子控件。
        /// 支持 Layout、ContentView、ScrollView、Border 等常见容器。
        /// </summary>
        public static IEnumerable<View> GetVisualChildren(View view)
        {
            if (view is Layout layout)
            {
                foreach (var child in layout.Children)
                {
                    if (child is View v)
                        yield return v;
                }
            }
            else if (view is ContentView contentView && contentView.Content is View cvContent)
            {
                yield return cvContent;
            }
            else if (view is ScrollView scrollView && scrollView.Content is View svContent)
            {
                yield return svContent;
            }
            else if (view is Border border && border.Content is View borderContent)
            {
                yield return borderContent;
            }
        }

        /// <summary>
        /// 递归获取指定控件的所有后代控件（深度优先）。
        /// </summary>
        public static IEnumerable<View> GetAllDescendants(View view)
        {
            foreach (var child in GetVisualChildren(view))
            {
                yield return child;
                foreach (var descendant in GetAllDescendants(child))
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// 递归获取指定 View 及其所有后代的全部控件（包含自身）。
        /// </summary>
        public static IEnumerable<View> GetAllControls(View view)
        {
            yield return view;
            foreach (var descendant in GetAllDescendants(view))
            {
                yield return descendant;
            }
        }

        /// <summary>
        /// 读取任意控件的当前值，根据控件类型返回对应的业务属性值。
        /// 不支持的控件类型返回 null。
        /// </summary>
        public static object? GetControlValue(View view)
        {
            if (view == null)
                throw new ArgumentNullException(nameof(view));

            return view switch
            {
                Entry entry => entry.Text,
                Label label => label.Text,
                Editor editor => editor.Text,
                Button button => button.Text,
                SearchBar searchBar => searchBar.Text,

                Slider slider => slider.Value,
                Stepper stepper => stepper.Value,
                ProgressBar progressBar => progressBar.Progress,

                Switch sw => sw.IsToggled,
                CheckBox checkBox => checkBox.IsChecked,
                RadioButton radioButton => radioButton.IsChecked,

                Picker picker => picker.SelectedItem ?? picker.SelectedIndex,
                DatePicker datePicker => datePicker.Date,
                TimePicker timePicker => timePicker.Time,

                _ => null
            };
        }

        /// <summary>
        /// 读取任意控件的值并转换为指定类型。
        /// 若类型不匹配或控件不支持读取，返回 default(T)。
        /// </summary>
        public static T? GetControlValue<T>(View view)
        {
            var value = GetControlValue(view);
            if (value == null)
                return default;

            try
            {
                return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return default;
            }
        }

        /// <summary>
        /// 尝试设置任意控件的值。如果控件类型不支持设置或值类型不匹配，返回 false。
        /// </summary>
        public static bool TrySetControlValue(View view, object? value)
        {
            if (view == null)
                throw new ArgumentNullException(nameof(view));

            try
            {
                switch (view)
                {
                    case Entry entry:
                        entry.Text = ToString(value);
                        return true;

                    case Label label:
                        label.Text = ToString(value);
                        return true;

                    case Editor editor:
                        editor.Text = ToString(value);
                        return true;

                    case Button button:
                        button.Text = ToString(value);
                        return true;

                    case SearchBar searchBar:
                        searchBar.Text = ToString(value);
                        return true;

                    case Slider slider when value is IConvertible:
                        slider.Value = (double)Convert.ChangeType(value, typeof(double), System.Globalization.CultureInfo.InvariantCulture);
                        return true;

                    case Stepper stepper when value is IConvertible:
                        stepper.Value = (double)Convert.ChangeType(value, typeof(double), System.Globalization.CultureInfo.InvariantCulture);
                        return true;

                    case ProgressBar progressBar when value is IConvertible:
                        progressBar.Progress = (double)Convert.ChangeType(value, typeof(double), System.Globalization.CultureInfo.InvariantCulture);
                        return true;

                    case Switch sw when value is bool boolVal:
                        sw.IsToggled = boolVal;
                        return true;

                    case CheckBox checkBox when value is bool boolVal:
                        checkBox.IsChecked = boolVal;
                        return true;

                    case RadioButton radioButton when value is bool boolVal:
                        radioButton.IsChecked = boolVal;
                        return true;

                    case Picker picker:
                        picker.SelectedItem = value;
                        return true;

                    case DatePicker datePicker when value is DateTime dateVal:
                        datePicker.Date = dateVal;
                        return true;

                    case DatePicker datePicker when value is IConvertible:
                        datePicker.Date = (DateTime)Convert.ChangeType(value, typeof(DateTime), System.Globalization.CultureInfo.InvariantCulture);
                        return true;

                    case TimePicker timePicker when value is TimeSpan timeVal:
                        timePicker.Time = timeVal;
                        return true;

                    case TimePicker timePicker when value is IConvertible:
                        timePicker.Time = (TimeSpan)Convert.ChangeType(value, typeof(TimeSpan), System.Globalization.CultureInfo.InvariantCulture);
                        return true;

                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region 实例方法：基于 _root 的查找（返回 ControlTreeItem）

        /// <summary>
        /// 在绑定的控件树中递归查找第一个 AutomationID 匹配指定值的控件。
        /// </summary>
        public ControlTreeItem? FindByAutomationId(string automationId)
        {
            if (string.IsNullOrWhiteSpace(automationId))
                return null;

            var view = GetAllDescendants(_root)
                .FirstOrDefault(c => string.Equals(c.AutomationId, automationId, StringComparison.Ordinal));

            return view != null ? new ControlTreeItem(view) : null;
        }

        /// <summary>
        /// 在绑定的控件树中递归查找 AutomationID 满足谓词的所有控件。
        /// </summary>
        public IEnumerable<ControlTreeItem> FindAllByAutomationId(Func<string, bool> predicate)
        {
            if (predicate == null)
                yield break;

            foreach (var control in GetAllDescendants(_root))
            {
                if (!string.IsNullOrWhiteSpace(control.AutomationId) && predicate(control.AutomationId))
                    yield return new ControlTreeItem(control);
            }
        }

        /// <summary>
        /// 在绑定的控件树中递归查找 AutomationID 以指定前缀开头的所有控件。
        /// </summary>
        public IEnumerable<ControlTreeItem> FindAllByAutomationIdPrefix(string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
                yield break;

            foreach (var control in GetAllDescendants(_root))
            {
                if (control.AutomationId?.StartsWith(prefix, StringComparison.Ordinal) == true)
                    yield return new ControlTreeItem(control);
            }
        }

        /// <summary>
        /// 将绑定控件树下所有带 AutomationID 的控件构造成字典 (AutomationId -> ControlTreeItem)。
        /// </summary>
        public Dictionary<string, ControlTreeItem> GetAutomationIdMap()
        {
            var map = new Dictionary<string, ControlTreeItem>(StringComparer.Ordinal);

            foreach (var control in GetAllDescendants(_root))
            {
                if (!string.IsNullOrWhiteSpace(control.AutomationId))
                {
                    map[control.AutomationId] = new ControlTreeItem(control);
                }
            }

            return map;
        }

        /// <summary>
        /// 获取绑定控件树下所有带 AutomationID 控件的扁平列表。
        /// </summary>
        public IEnumerable<ControlTreeItem> GetAutomationIdEntries(bool includeControlsWithoutAutomationId = false)
        {
            foreach (var control in GetAllDescendants(_root))
            {
                if (!string.IsNullOrWhiteSpace(control.AutomationId))
                    yield return new ControlTreeItem(control);
                else if (includeControlsWithoutAutomationId)
                    yield return new ControlTreeItem(control, $"ControlTreeItem-{control.GetType().Name}-{control.Id}");
            }
        }

        /// <summary>
        /// 获取绑定控件树下所有没有 AutomationID 的控件。
        /// </summary>
        public IEnumerable<ControlTreeItem> GetControlsWithoutAutomationId()
        {
            return GetAllDescendants(_root)
                .Where(c => string.IsNullOrWhiteSpace(c.AutomationId))
                .Select(c => new ControlTreeItem(c));
        }

        #endregion

        #region 实例方法：基于 AutomationID 的读取/写入

        /// <summary>
        /// 按 AutomationID 查找控件并读取其值。
        /// </summary>
        /// <returns>找到则返回值，否则返回 null。</returns>
        public object? GetControlValue(string automationId)
        {
            return FindByAutomationId(automationId)?.Value;
        }

        /// <summary>
        /// 按 AutomationID 查找控件，读取其值并转换为指定类型。
        /// </summary>
        public T? GetControlValue<T>(string automationId) where T : notnull
        {
            var item = FindByAutomationId(automationId);
            return item != null ? item.GetValue<T>() : default;
        }

        /// <summary>
        /// 按 AutomationID 查找控件，并尝试设置其值。
        /// </summary>
        /// <returns>找到控件且设置成功返回 true；否则返回 false。</returns>
        public bool TrySetControlValue(string automationId, object? value)
        {
            return FindByAutomationId(automationId)?.Set(value) ?? false;
        }

        #endregion

        #region 实例方法：批量操作

        /// <summary>
        /// 批量设置多个控件的值。
        /// key 为 AutomationID，value 为要设置的值。
        /// 返回实际设置成功的数量。
        /// </summary>
        public int BatchSetValues(IDictionary<string, object?> keyValues)
        {
            if (keyValues == null)
                throw new ArgumentNullException(nameof(keyValues));

            int successCount = 0;
            foreach (var kvp in keyValues)
            {
                if (TrySetControlValue(kvp.Key, kvp.Value))
                    successCount++;
            }
            return successCount;
        }

        /// <summary>
        /// 批量读取多个控件的值。
        /// key 为 AutomationID，value 为读取到的值（未找到或类型不匹配时为 default）。
        /// </summary>
        public Dictionary<string, object?> BatchGetValues(IEnumerable<string> automationIds)
        {
            if (automationIds == null)
                throw new ArgumentNullException(nameof(automationIds));

            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var id in automationIds)
            {
                result[id] = GetControlValue(id);
            }
            return result;
        }

        /// <summary>
        /// 获取绑定控件树下所有带 AutomationID 的控件，以 AutomationID -> ControlTreeItem 字典形式返回。
        /// </summary>
        public Dictionary<string, ControlTreeItem> GetAllItems(bool includeControlsWithoutAutomationId = false)
        {
            var result = new Dictionary<string, ControlTreeItem>(StringComparer.Ordinal);
            foreach (var item in GetAutomationIdEntries(includeControlsWithoutAutomationId))
            {
                if (item.AutomationID != null)
                    result[item.AutomationID] = item;
            }
            return result;
        }

        /// <summary>
        /// 获取所有控件的值的字典 (AutomationID -> 当前值)。
        /// </summary>
        public Dictionary<string, object?> GetAllValues()
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var item in GetAutomationIdEntries())
            {
                if (item.AutomationID != null)
                    result[item.AutomationID] = item.Value;
            }
            return result;
        }

        #endregion

        #region 树结构导出

        /// <summary>
        /// 将绑定的控件树以文本形式导出（用于调试）。
        /// </summary>
        public string DumpTree()
        {
            var sb = new StringBuilder();
            DumpTreeInternal(_root, sb, 0);
            return sb.ToString();
        }

        private static void DumpTreeInternal(View view, StringBuilder sb, int indent)
        {
            var indentStr = new string(' ', indent * 2);
            var typeName = view.GetType().Name;
            var autoId = !string.IsNullOrWhiteSpace(view.AutomationId) ? $" [\"{view.AutomationId}\"]" : "";
            var value = GetControlValue(view);
            var valueStr = value != null ? $" = {value}" : "";
            sb.AppendLine($"{indentStr}- {typeName}{autoId}{valueStr}");

            foreach (var child in GetVisualChildren(view))
            {
                DumpTreeInternal(child, sb, indent + 1);
            }
        }

        /// <summary>
        /// 重新绑定到新的根控件。
        /// </summary>
        public void Rebind(View newRoot)
        {
            _root = newRoot ?? throw new ArgumentNullException(nameof(newRoot));
        }

        #endregion

        #region 树结构枚举（ControlTreeCollectionItem）

        /// <summary>
        /// 将指定的 View 构造成带有完整 Children 子树的 ControlTreeCollectionItem。
        /// 根节点始终返回 ControlTreeCollectionItem（即使它没有子控件）。
        /// </summary>
        /// <param name="view">任意 View（通常是一个 Layout 容器）。</param>
        public static ControlTreeCollectionItem GetVisualTree(View view)
        {
            ArgumentNullException.ThrowIfNull(view);
            return new ControlTreeCollectionItem(view);
        }

        /// <summary>
        /// 将绑定的根控件构造成带有完整 Children 子树的 ControlTreeCollectionItem。
        /// </summary>
        public ControlTreeCollectionItem GetVisualTree()
        {
            return GetVisualTree(_root);
        }

        /// <summary>
        /// 在绑定的控件树中查找指定 AutomationID 的控件，
        /// 如果找到的控件是容器则返回 ControlTreeCollectionItem（含子树），
        /// 否则返回 ControlTreeItem。
        /// </summary>
        /// <param name="automationId">要查找的 AutomationID。</param>
        /// <param name="includeRoot">如果为 true，也会检查根控件本身是否匹配。</param>
        /// <returns>找到则返回 ControlTreeItem 或其子类，未找到返回 null。</returns>
        public ControlTreeItem? FindTreeByAutomationId(string automationId, bool includeRoot = false)
        {
            if (string.IsNullOrWhiteSpace(automationId))
                return null;

            // 是否检查根控件
            if (includeRoot && string.Equals(_root.AutomationId, automationId, StringComparison.Ordinal))
            {
                return _root is Layout or ContentView or ScrollView or Border
                    ? new ControlTreeCollectionItem(_root)
                    : new ControlTreeItem(_root);
            }

            // 在子孙中查找
            var view = GetAllDescendants(_root)
                .FirstOrDefault(c => string.Equals(c.AutomationId, automationId, StringComparison.Ordinal));

            if (view == null)
                return null;

            return view is Layout or ContentView or ScrollView or Border
                ? new ControlTreeCollectionItem(view)
                : new ControlTreeItem(view);
        }

        /// <summary>
        /// 按 AutomationID 查找并返回该节点及其子树的结构化树（JSON 友好）。
        /// 返回的 ControlTreeItem 如果是容器则 Children 会被填充。
        /// </summary>
        /// <param name="automationId">要查找的根节点的 AutomationID。</param>
        /// <param name="includeRoot">是否也检查根控件本身。</param>
        /// <returns>找到则返回节点树，未找到返回 null。</returns>
        public ControlTreeItem? GetSubTreeByAutomationId(string automationId, bool includeRoot = false)
        {
            return FindTreeByAutomationId(automationId, includeRoot);
        }

        #endregion

        #region 辅助方法

        private static string? ToString(object? value)
        {
            return value?.ToString();
        }

        /// <summary>
        /// 判断控件类型是否支持写入（即 TrySetControlValue 对该类型是否有效）。
        /// </summary>
        private static bool IsControlTypeWritable(View view)
        {
            return view switch
            {
                Entry or Label or Editor or Button or SearchBar
                    or Slider or Stepper or ProgressBar
                    or Switch or CheckBox or RadioButton
                    or Picker or DatePicker or TimePicker
                    or ActivityIndicator => true,
                _ => false
            };
        }

        #endregion

        /// <summary>
        /// 控件树项，包装一个 View 并提供类型信息、当前值及写入能力。
        /// </summary>
        public class ControlTreeItem
        {
            private readonly View _view;
            private string? _idOverride = null;

            internal ControlTreeItem(View view)
            {
                _view = view ?? throw new ArgumentNullException(nameof(view));
            }
            internal ControlTreeItem(View view, string id)
            {
                _view = view ?? throw new ArgumentNullException(nameof(view));
                _idOverride = id;
            }

            /// <summary>对应的原始 View。</summary>
            [JsonIgnore()]
            public View View => _view;

            /// <summary>控件类型名称（如 "Entry"、"Slider"）。</summary>
            public string ControlType => _view.GetType().Name;

            /// <summary>控件的 AutomationID，可能为 null。</summary>
            public string? AutomationID => _idOverride ?? _view.AutomationId;

            /// <summary>控件的当前值。</summary>
            public object? Value => GetControlValue(_view);

            /// <summary>当前值的类型名称，无值时返回 "null"。</summary>
            public string ValueType => Value?.GetType().Name ?? "null";

            /// <summary>该控件类型是否支持写入。</summary>
            public bool IsWritable => IsControlTypeWritable(_view);

            /// <summary>
            /// 读取当前值并转换为指定类型。
            /// </summary>
            public T? GetValue<T>() where T : notnull
            {
                return GetControlValue<T>(_view);
            }

            /// <summary>
            /// 尝试设置控件的值。如果控件类型不支持写入或值类型不匹配，返回 false。
            /// </summary>
            public bool Set(object? newValue)
            {
                if (!IsWritable) throw new InvalidOperationException("This is a read-only control.");
                return TrySetControlValue(_view, newValue);
            }

            /// <summary>返回控件的摘要信息，格式："{ControlType} [AutomationID] = 当前值"。</summary>
            public override string ToString()
            {
                var id = AutomationID != null ? $" \"{AutomationID}\"" : "";
                var val = Value;
                return val != null
                    ? $"{ControlType}{id} = {val} ({ValueType}, Writable: {IsWritable})"
                    : $"{ControlType}{id} ({ValueType})";
            }
        }

        public class ControlTreeCollectionItem : ControlTreeItem
        {
            /// <summary>
            /// 子控件字典，Key 为 AutomationID（无 AutomationID 则自动生成 "TypeName[index]"）。
            /// 若子控件本身也是容器，则会递归创建 ControlTreeCollectionItem。
            /// </summary>
            public Dictionary<string, ControlTreeItem> Children { get; private set; }

            /// <summary>
            /// 创建一个 ControlTreeCollectionItem，并自动枚举其所有可视子控件到 Children。
            /// </summary>
            public ControlTreeCollectionItem(View view) : base(view)
            {
                Children = new Dictionary<string, ControlTreeItem>(StringComparer.Ordinal);
                PopulateChildren(view);
            }

            /// <summary>
            /// 创建一个 ControlTreeCollectionItem，使用指定的 id 覆盖 AutomationID。
            /// </summary>
            public ControlTreeCollectionItem(View view, string id) : base(view, id)
            {
                Children = new Dictionary<string, ControlTreeItem>(StringComparer.Ordinal);
                PopulateChildren(view);
            }

            /// <summary>
            /// 枚举 view 的直接可视子控件并填充到 Children 字典中。
            /// 如果子控件自身也是容器（Layout / ContentView / ScrollView / Border），
            /// 则递归为其创建 ControlTreeCollectionItem。
            /// </summary>
            private void PopulateChildren(View view)
            {
                foreach (var child in GetVisualChildren(view))
                {
                    // 生成字典 key：优先使用 AutomationID，否则按 "TypeName[index]"
                    string key;
                    if (!string.IsNullOrWhiteSpace(child.AutomationId))
                    {
                        key = child.AutomationId;
                    }
                    else
                    {
                        key = $"Control-{child.GetType().Name}-{child.Id}";
                    }

                    // 若子控件也是容器则递归创建 CollectionItem
                    if (child is Layout || child is ContentView || child is ScrollView || child is Border)
                    {
                        Children[key] = new ControlTreeCollectionItem(child);
                    }
                    else
                    {
                        Children[key] = new ControlTreeItem(child);
                    }

                }
            }
        }
    }
}
