using System.Windows;
using System.Windows.Media;

namespace AMCStudios.Installer
{
    // property that tells us whether we want to scale this visual or not -E
    public static class ResizeBehavior
    {
        public static readonly DependencyProperty ResizeOnWindowProperty =
            DependencyProperty.RegisterAttached(
                "ResizeOnWindow",
                typeof(bool),
                typeof(ResizeBehavior),
                new PropertyMetadata(false, OnResizeOnWindowChanged));

        public static void SetResizeOnWindow(DependencyObject element, bool value) =>
            element.SetValue(ResizeOnWindowProperty, value);

        public static bool GetResizeOnWindow(DependencyObject element) =>
            (bool)element.GetValue(ResizeOnWindowProperty);

        private static void OnResizeOnWindowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement fe && (bool)e.NewValue)
            {
                fe.Loaded -= Fe_Loaded;
                fe.Loaded += Fe_Loaded;
            }
        }

        private static void Fe_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                EnsureScaleTransform(fe);
            }
        }

        public static void ApplyScaleToOptedElements(Window host, double scaleX, double scaleY)
        {
            if (host == null) return;
            if (!(host.Content is DependencyObject root)) return;

            var list = new List<FrameworkElement>();
            FindOptedElements(root, list);
            foreach (var fe in list)
            {
                var st = EnsureScaleTransform(fe);
                if (st != null)
                {
                    st.ScaleX = scaleX;
                    st.ScaleY = scaleY;
                }
            }
        }

        public static void ClearScale(Window host)
        {
            if (host == null) return;
            if (!(host.Content is DependencyObject root)) return;

            var list = new List<FrameworkElement>();
            FindOptedElements(root, list);
            foreach (var fe in list)
            {
                var tg = fe.RenderTransform as TransformGroup;
                if (tg != null)
                {
                    foreach (var t in tg.Children)
                    {
                        if (t is ScaleTransform st)
                        {
                            st.ScaleX = 1.0;
                            st.ScaleY = 1.0;
                        }
                    }
                }
                else if (fe.RenderTransform is ScaleTransform st)
                {
                    st.ScaleX = 1.0;
                    st.ScaleY = 1.0;
                }
            }
        }

        private static void FindOptedElements(DependencyObject node, List<FrameworkElement> list)
        {
            if (node == null) return;

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement fe)
                {
                    if (GetResizeOnWindow(fe)) list.Add(fe);
                }
                FindOptedElements(child, list);
            }
        }

        private static ScaleTransform EnsureScaleTransform(FrameworkElement fe)
        {
            if (fe == null) return null;

            // if we see a group, is there actually a scale transform? -E
            if (fe.RenderTransform is TransformGroup tg)
            {
                foreach (var t in tg.Children)
                {
                    if (t is ScaleTransform st) return st;
                }

                var newSt = new ScaleTransform(1, 1);
                // dont fuck with this or it makes scaling wonk as fuck -E
                tg.Children.Insert(0, newSt);
                fe.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
                return newSt;
            }

            // only one? -E
            if (fe.RenderTransform is ScaleTransform singleSt)
            {
                return singleSt;
            }

            // put that shit in a group. -E
            var existing = fe.RenderTransform;
            var group = new TransformGroup();
            var stNew = new ScaleTransform(1, 1);
            group.Children.Add(stNew);
            if (existing != null && !(existing is MatrixTransform && ((MatrixTransform)existing).Matrix.IsIdentity))
            {
                group.Children.Add(existing);
            }
            fe.RenderTransform = group;
            fe.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            return stNew;
        }
    }
}