using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace AMCStudios.Installer
{
    public static class BadgeVisual
    {
        public const string OwnerBadge = "owner";
        public const string AmcDevBadge = "amc-dev";
        public const string ModDevBadge = "mod-dev";
        public const string OfficialBadge = "official";
        public const string PartnerBadge = "partner";
        public const string BugHunterBadge = "bug-hunter";
        public const string PopularBadge = "popular";
        public const string TyBadge = "ty";

        private static readonly string[] Hierarchy =
        {
            TyBadge, OwnerBadge, AmcDevBadge, ModDevBadge, OfficialBadge,
            PartnerBadge, BugHunterBadge, PopularBadge
        };

        public static readonly System.Windows.Media.Color OwnerColor = System.Windows.Media.Color.FromRgb(0xFF, 0xD7, 0x00);
        public static readonly System.Windows.Media.Color AmcDevColor = System.Windows.Media.Color.FromRgb(0xE5, 0x39, 0x35);
        public static readonly System.Windows.Media.Color ModDevColor = System.Windows.Media.Color.FromRgb(0x9C, 0x27, 0xB0);
        public static readonly System.Windows.Media.Color OfficialColor = System.Windows.Media.Color.FromRgb(0x1D, 0xA1, 0xF2);
        public static readonly System.Windows.Media.Color PartnerColor = System.Windows.Media.Color.FromRgb(0x7B, 0x2F, 0xFF);
        public static readonly System.Windows.Media.Color BugHunterColor = System.Windows.Media.Color.FromRgb(0x43, 0xA0, 0x47);
        public static readonly System.Windows.Media.Color PopularColor = System.Windows.Media.Color.FromRgb(0xE6, 0xE6, 0xE6);
        public static readonly System.Windows.Media.Color TyPink = System.Windows.Media.Color.FromRgb(0xFF, 0x5C, 0xA0);

        private static readonly LinearGradientBrush TyOceanBrush = CreateTyOceanBrush();

        private static LinearGradientBrush CreateTyOceanBrush()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0, 0.5),
                EndPoint = new System.Windows.Point(1, 0.5),
                SpreadMethod = GradientSpreadMethod.Repeat,
            };
            var stops = new[]
            {
                new GradientStop(TyPink, 0.00),
                new GradientStop(TyPink, 0.30),
                new GradientStop(Colors.White, 0.50),
                new GradientStop(TyPink, 0.70),
                new GradientStop(TyPink, 1.00),
            };
            foreach (var s in stops) brush.GradientStops.Add(s);

            var duration = new Duration(TimeSpan.FromSeconds(2.5));
            foreach (var s in stops)
            {
                s.BeginAnimation(GradientStop.OffsetProperty,
                    new DoubleAnimation(s.Offset, s.Offset + 1.0, duration)
                    {
                        RepeatBehavior = RepeatBehavior.Forever,
                    });
            }
            return brush;
        }

        private static readonly SolidColorBrush OwnerBrush = new SolidColorBrush(OwnerColor);
        private static readonly SolidColorBrush AmcDevBrush = new SolidColorBrush(AmcDevColor);
        private static readonly SolidColorBrush ModDevBrush = new SolidColorBrush(ModDevColor);
        private static readonly SolidColorBrush OfficialBrush = new SolidColorBrush(OfficialColor);
        private static readonly SolidColorBrush PartnerBrush = new SolidColorBrush(PartnerColor);
        private static readonly SolidColorBrush BugHunterBrush = new SolidColorBrush(BugHunterColor);
        private static readonly SolidColorBrush PopularBrush = new SolidColorBrush(PopularColor);

        private const string CrownData = "M5,17 L3,6.5 L8.5,11.5 L12,4.5 L15.5,11.5 L21,6.5 L19,17 Z";
        private const string CheckData = "M6,12.5 L10,16.5 L18,7.5";
        private const string StarData = "M12,2.6 L14.6,9.2 L21.4,9.2 L15.9,13.3 L18.4,19.9 L12,15.8 L5.6,19.9 L8.1,13.3 L2.6,9.2 L9.4,9.2 Z";
        private const string HeartData = "M12,20.8 C7.2,16.6 2.8,13.2 2.8,9.0 C2.8,6.3 4.9,4.2 7.6,4.2 C9.3,4.2 10.9,5.2 12,6.9 C13.1,5.2 14.7,4.2 16.4,4.2 C19.1,4.2 21.2,6.3 21.2,9.0 C21.2,13.2 16.8,16.6 12,20.8 Z";

        public static string RoleLabel(string role)
        {
            switch (role)
            {
                case OwnerBadge: return "Owner";
                case AmcDevBadge: return "AMC Store Developer";
                case ModDevBadge: return "Mod Developer";
                case OfficialBadge: return "Official";
                case PartnerBadge: return "AMC Store Partner";
                case BugHunterBadge: return "Bug Hunter";
                case PopularBadge: return "Popular";
                case TyBadge: return "Ty";
                default: return "";
            }
        }

        public static bool IsRole(string role) => role != null &&
            (role == OwnerBadge || role == AmcDevBadge || role == ModDevBadge ||
             role == OfficialBadge || role == PartnerBadge || role == BugHunterBadge ||
             role == PopularBadge || role == TyBadge);

        public static bool HasBadge(IEnumerable<string> badges)
        {
            if (badges == null) return false;
            foreach (var b in badges)
                if (IsRole(b)) return true;
            return false;
        }

        public static System.Windows.Media.Brush UsernameBrush(IEnumerable<string> badges)
        {
            if (badges == null) return null;

            for (int i = 0; i < Hierarchy.Length; i++)
            {
                if (Contains(badges, Hierarchy[i]))
                    return BrushFor(Hierarchy[i]);
            }
            return null;
        }

        private static System.Windows.Media.Brush BrushFor(string role)
        {
            switch (role)
            {
                case OwnerBadge: return OwnerBrush;
                case AmcDevBadge: return AmcDevBrush;
                case ModDevBadge: return ModDevBrush;
                case OfficialBadge: return OfficialBrush;
                case PartnerBadge: return PartnerBrush;
                case BugHunterBadge: return BugHunterBrush;
                case PopularBadge: return PopularBrush;
                case TyBadge: return TyOceanBrush;
                default: return null;
            }
        }

        private static bool Contains(IEnumerable<string> badges, string match)
        {
            if (badges == null) return false;
            foreach (var b in badges) if (b == match) return true;
            return false;
        }

        public static FrameworkElement BuildIconRow(IEnumerable<string> badges)
        {
            if (badges == null) return null;
            var icons = new List<FrameworkElement>();
            var roles = new List<string>();

            for (int i = 0; i < Hierarchy.Length; i++)
            {
                if (Contains(badges, Hierarchy[i]))
                {
                    icons.Add(MakeIcon(Hierarchy[i]));
                    roles.Add(Hierarchy[i]);
                }
            }
            if (icons.Count == 0) return null;

            var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            for (int i = 0; i < icons.Count; i++)
            {
                if (i > 0) icons[i].Margin = new Thickness(5, 0, 0, 0);
                icons[i].ToolTip = RoleLabel(roles[i]);
                panel.Children.Add(icons[i]);
            }
            return panel;
        }

        public static FrameworkElement MakeIcon(string role)
        {
            switch (role)
            {
                case OwnerBadge: return MakeCrown();
                case AmcDevBadge: return MakeCheck(AmcDevBrush);
                case ModDevBadge: return MakeCheck(ModDevBrush);
                case OfficialBadge: return MakeBadgeCheck(OfficialBrush);
                case PartnerBadge: return MakeStar(PartnerBrush);
                case BugHunterBadge: return MakeBug(BugHunterBrush);
                case PopularBadge: return MakeCheck(PopularBrush);
                case TyBadge: return MakeHeart();
                default: return null;
            }
        }

        private static FrameworkElement MakeCrown()
        {
            var path = new Path
            {
                Data = Geometry.Parse(CrownData),
                Fill = OwnerBrush,
                Stretch = Stretch.Uniform,
                Width = 15,
                Height = 15
            };
            return Host(path, 15, 15);
        }

        private static FrameworkElement MakeCheck(SolidColorBrush brush)
        {
            var path = new Path
            {
                Data = Geometry.Parse(CheckData),
                Stroke = brush,
                StrokeThickness = 2.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.Uniform
            };
            return Host(path, 15, 15);
        }

        private static FrameworkElement MakeBadgeCheck(SolidColorBrush brush)
        {
            var grid = new Grid { Width = 15, Height = 15 };
            var badge = new Ellipse { Fill = brush };
            var check = new Path
            {
                Data = Geometry.Parse(CheckData),
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Margin = new Thickness(2.5),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Stretch = Stretch.Uniform
            };
            grid.Children.Add(badge);
            grid.Children.Add(check);
            return Host(grid, 15, 15);
        }

        private static FrameworkElement MakeStar(SolidColorBrush brush)
        {
            var path = new Path
            {
                Data = Geometry.Parse(StarData),
                Fill = brush,
                Stretch = Stretch.Uniform,
                Width = 16,
                Height = 16
            };
            return Host(path, 16, 16);
        }

        private static FrameworkElement MakeHeart()
        {
            var glow = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = TyPink,
                BlurRadius = 9,
                ShadowDepth = 0,
                Opacity = 0.95,
            };
            var path = new Path
            {
                Data = Geometry.Parse(HeartData),
                Fill = new SolidColorBrush(TyPink),
                Stretch = Stretch.Uniform,
                Width = 15,
                Height = 15,
                Effect = glow,
            };
            return Host(path, 15, 15);
        }

        private static FrameworkElement MakeBug(SolidColorBrush brush)
        {
            var grid = new Grid { Width = 15, Height = 15 };

            var body = new Ellipse
            {
                Fill = brush,
                Width = 8,
                Height = 8,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var ant = new Path
            {
                Data = Geometry.Parse("M5.5,2.5 C4,4 4,5 5,6.5 M9.5,2.5 C11,4 11,5 10,6.5"),
                Stroke = brush,
                StrokeThickness = 1.3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            var eyes = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
            var eyeL = new Ellipse { Fill = System.Windows.Media.Brushes.White, Width = 1.7, Height = 1.7, Margin = new Thickness(0, 2.4, 1.4, 0) };
            var eyeR = new Ellipse { Fill = System.Windows.Media.Brushes.White, Width = 1.7, Height = 1.7, Margin = new Thickness(0, 2.4, 0, 0) };
            eyes.Children.Add(eyeL);
            eyes.Children.Add(eyeR);

            grid.Children.Add(ant);
            grid.Children.Add(body);
            grid.Children.Add(eyes);
            return Host(grid, 15, 15);
        }

        private static FrameworkElement Host(FrameworkElement inner, double w, double h)
        {
            var host = new Grid { Width = w, Height = h };
            inner.VerticalAlignment = VerticalAlignment.Center;
            inner.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            host.Children.Add(inner);
            return host;
        }
    }
}