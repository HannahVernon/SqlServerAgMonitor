using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using SqlAgMonitor.Core.Models;

namespace SqlAgMonitor.Controls;

public class TopologyControl : Control
{
    public static readonly StyledProperty<AvailabilityGroupInfo?> AgInfoProperty =
        AvaloniaProperty.Register<TopologyControl, AvailabilityGroupInfo?>(nameof(AgInfo));

    public static readonly StyledProperty<DistributedAgInfo?> DagInfoProperty =
        AvaloniaProperty.Register<TopologyControl, DistributedAgInfo?>(nameof(DagInfo));

    public static readonly StyledProperty<string?> SelectedReplicaProperty =
        AvaloniaProperty.Register<TopologyControl, string?>(nameof(SelectedReplica),
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public AvailabilityGroupInfo? AgInfo
    {
        get => GetValue(AgInfoProperty);
        set => SetValue(AgInfoProperty, value);
    }

    public DistributedAgInfo? DagInfo
    {
        get => GetValue(DagInfoProperty);
        set => SetValue(DagInfoProperty, value);
    }

    public string? SelectedReplica
    {
        get => GetValue(SelectedReplicaProperty);
        set => SetValue(SelectedReplicaProperty, value);
    }

    public event EventHandler<string?>? ReplicaSelected;

    // Layout constants
    private const double NodeWidth = 180;
    private const double NodeHeight = 120;
    private const double NodeSpacingX = 80;
    private const double NodeSpacingY = 40;
    private const double ParticleRadius = 3;
    private const double ParticleSpeed = 0.02;
    private const double DagGroupPadding = 20;

    // Animation state
    private readonly DispatcherTimer _animationTimer;
    private readonly List<NodeLayout> _nodeLayouts = new();
    private readonly List<ArrowLayout> _arrowLayouts = new();
    private readonly List<DagGroupLayout> _dagGroupLayouts = new();
    private double _animationTime;

    // Colors
    private static readonly Color GreenColor = Color.Parse("#4CAF50");
    private static readonly Color YellowColor = Color.Parse("#FFC107");
    private static readonly Color OrangeColor = Color.Parse("#FF9800");
    private static readonly Color RedColor = Color.Parse("#F44336");
    private static readonly Color GrayColor = Color.Parse("#9E9E9E");

    public TopologyControl()
    {
        _animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _animationTimer.Tick += OnAnimationTick;
    }

    static TopologyControl()
    {
        AffectsRender<TopologyControl>(AgInfoProperty, DagInfoProperty, SelectedReplicaProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == AgInfoProperty || change.Property == DagInfoProperty)
        {
            RecalculateLayout();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        RecalculateLayout();
        _animationTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _animationTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        _animationTime += ParticleSpeed;
        if (_animationTime > 1.0)
            _animationTime -= 1.0;

        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pos = e.GetPosition(this);

        string? clicked = null;
        foreach (var node in _nodeLayouts)
        {
            if (node.Bounds.Contains(pos))
            {
                clicked = node.ReplicaServerName;
                break;
            }
        }

        SelectedReplica = clicked;
        ReplicaSelected?.Invoke(this, clicked);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_nodeLayouts.Count == 0)
            return new Size(400, 200);

        double maxX = _nodeLayouts.Max(n => n.Bounds.Right);
        double maxY = _nodeLayouts.Max(n => n.Bounds.Bottom);

        if (_dagGroupLayouts.Count > 0)
        {
            maxX = Math.Max(maxX, _dagGroupLayouts.Max(g => g.Bounds.Right));
            maxY = Math.Max(maxY, _dagGroupLayouts.Max(g => g.Bounds.Bottom));
        }

        return new Size(
            Math.Min(maxX + DagGroupPadding, availableSize.Width),
            Math.Min(maxY + DagGroupPadding, availableSize.Height));
    }

    private void RecalculateLayout()
    {
        _nodeLayouts.Clear();
        _arrowLayouts.Clear();
        _dagGroupLayouts.Clear();

        if (DagInfo != null)
            LayoutDag(DagInfo);
        else if (AgInfo != null)
            LayoutAg(AgInfo, DagGroupPadding, DagGroupPadding);

        InvalidateMeasure();
        InvalidateVisual();
    }

    private void LayoutAg(AvailabilityGroupInfo ag, double offsetX, double offsetY)
    {
        var primary = ag.Replicas.FirstOrDefault(r => r.Role == ReplicaRole.Primary);
        var secondaries = ag.Replicas.Where(r => r.Role != ReplicaRole.Primary).ToList();

        double totalHeight = Math.Max(1, secondaries.Count) * (NodeHeight + NodeSpacingY) - NodeSpacingY;
        double primaryY = offsetY + (totalHeight - NodeHeight) / 2;

        if (primary != null)
        {
            var primaryLayout = new NodeLayout
            {
                Bounds = new Rect(offsetX, primaryY, NodeWidth, NodeHeight),
                Replica = primary,
                ReplicaServerName = primary.ReplicaServerName,
                IsPrimary = true
            };
            _nodeLayouts.Add(primaryLayout);

            double secondaryX = offsetX + NodeWidth + NodeSpacingX;
            for (int i = 0; i < secondaries.Count; i++)
            {
                double y = offsetY + i * (NodeHeight + NodeSpacingY);
                var secLayout = new NodeLayout
                {
                    Bounds = new Rect(secondaryX, y, NodeWidth, NodeHeight),
                    Replica = secondaries[i],
                    ReplicaServerName = secondaries[i].ReplicaServerName,
                    IsPrimary = false
                };
                _nodeLayouts.Add(secLayout);

                decimal maxLsnDiff = secondaries[i].DatabaseStates.Count > 0
                    ? secondaries[i].DatabaseStates.Max(d => d.LsnDifferenceFromPrimary)
                    : 0;
                bool isDisconnected = secondaries[i].ConnectedState == ConnectedState.Disconnected;

                _arrowLayouts.Add(new ArrowLayout
                {
                    From = new Point(primaryLayout.Bounds.Right, primaryLayout.Bounds.Center.Y),
                    To = new Point(secLayout.Bounds.Left, secLayout.Bounds.Center.Y),
                    IsSynchronous = secondaries[i].AvailabilityMode == AvailabilityMode.SynchronousCommit,
                    HealthLevel = HealthLevelExtensions.FromLsnDifference(maxLsnDiff, isDisconnected)
                });
            }
        }
        else if (ag.Replicas.Count > 0)
        {
            // No primary found — lay out all replicas in a row
            for (int i = 0; i < ag.Replicas.Count; i++)
            {
                double x = offsetX + i * (NodeWidth + NodeSpacingX);
                _nodeLayouts.Add(new NodeLayout
                {
                    Bounds = new Rect(x, offsetY, NodeWidth, NodeHeight),
                    Replica = ag.Replicas[i],
                    ReplicaServerName = ag.Replicas[i].ReplicaServerName,
                    IsPrimary = false
                });
            }
        }
    }

    private void LayoutDag(DistributedAgInfo dag)
    {
        double x = DagGroupPadding;
        var memberLayouts = new List<(DistributedAgMember Member, double StartX, double EndX)>();

        foreach (var member in dag.Members)
        {
            if (member.LocalAgInfo == null)
                continue;

            double groupStartX = x;
            int nodesBefore = _nodeLayouts.Count;

            LayoutAg(member.LocalAgInfo, x + DagGroupPadding, DagGroupPadding * 3);

            // Calculate the bounding box for this member group
            var memberNodes = _nodeLayouts.Skip(nodesBefore).ToList();
            if (memberNodes.Count > 0)
            {
                double groupRight = memberNodes.Max(n => n.Bounds.Right) + DagGroupPadding;
                double groupBottom = memberNodes.Max(n => n.Bounds.Bottom) + DagGroupPadding;

                _dagGroupLayouts.Add(new DagGroupLayout
                {
                    Bounds = new Rect(groupStartX, DagGroupPadding, groupRight - groupStartX, groupBottom),
                    MemberAgName = member.MemberAgName,
                    DistributedRole = member.DistributedRole
                });

                memberLayouts.Add((member, groupStartX, groupRight));
                x = groupRight + DagGroupPadding * 2;
            }
        }

        // Draw arrows between DAG member groups (primary AG → secondary AG)
        var forwarder = dag.Members.FirstOrDefault(m => m.DistributedRole == ReplicaRole.Primary);
        var receivers = dag.Members.Where(m => m.DistributedRole != ReplicaRole.Primary).ToList();

        if (forwarder?.LocalAgInfo != null)
        {
            // Find the primary replica within the forwarder's local AG for arrow positioning
            var forwarderNode = _nodeLayouts.FirstOrDefault(n =>
                forwarder.LocalAgInfo.Replicas.Any(r =>
                    r.ReplicaServerName == n.ReplicaServerName && r.Role == ReplicaRole.Primary))
                ?? _nodeLayouts.FirstOrDefault(n =>
                    forwarder.LocalAgInfo.Replicas.Any(r =>
                        r.ReplicaServerName == n.ReplicaServerName));

            foreach (var receiver in receivers)
            {
                if (receiver.LocalAgInfo == null)
                    continue;

                // Find the primary replica within the receiver's local AG, or any replica
                var receiverNode = _nodeLayouts.FirstOrDefault(n =>
                    receiver.LocalAgInfo.Replicas.Any(r =>
                        r.ReplicaServerName == n.ReplicaServerName && r.Role == ReplicaRole.Primary))
                    ?? _nodeLayouts.FirstOrDefault(n =>
                        receiver.LocalAgInfo.Replicas.Any(r =>
                            r.ReplicaServerName == n.ReplicaServerName));

                if (forwarderNode != null && receiverNode != null)
                {
                    // Compute max LSN difference across the receiver member's databases
                    decimal maxLsnDiff = 0;
                    foreach (var replica in receiver.LocalAgInfo.Replicas)
                    {
                        if (replica.DatabaseStates.Count > 0)
                            maxLsnDiff = Math.Max(maxLsnDiff,
                                replica.DatabaseStates.Max(d => d.LsnDifferenceFromPrimary));
                    }

                    _arrowLayouts.Add(new ArrowLayout
                    {
                        From = new Point(forwarderNode.Bounds.Right + 10, forwarderNode.Bounds.Center.Y),
                        To = new Point(receiverNode.Bounds.Left - 10, receiverNode.Bounds.Center.Y),
                        IsSynchronous = forwarder.AvailabilityMode == AvailabilityMode.SynchronousCommit,
                        HealthLevel = HealthLevelExtensions.FromLsnDifference(maxLsnDiff),
                        IsDagLink = true
                    });
                }
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Draw DAG group containers
        foreach (var group in _dagGroupLayouts)
            DrawDagGroup(context, group);

        // Draw arrows
        foreach (var arrow in _arrowLayouts)
            DrawArrow(context, arrow);

        // Draw particles along arrows
        foreach (var arrow in _arrowLayouts)
            DrawParticles(context, arrow);

        // Draw node cards
        foreach (var node in _nodeLayouts)
            DrawNode(context, node);
    }

    private static void DrawDagGroup(DrawingContext context, DagGroupLayout group)
    {
        var borderColor = group.DistributedRole == ReplicaRole.Primary
            ? Color.Parse("#3A5A3A")
            : Color.Parse("#3A3A5A");

        var bgBrush = new SolidColorBrush(Color.Parse("#1A1A2E"), 0.5);
        var borderPen = new Pen(new SolidColorBrush(borderColor), 1.5,
            new DashStyle(new double[] { 6, 3 }, 0));

        context.DrawRectangle(bgBrush, borderPen, new RoundedRect(group.Bounds, 12));

        // Group label
        var labelText = CreateFormattedText(
            group.MemberAgName,
            10,
            new SolidColorBrush(Color.Parse("#888899")),
            FontWeight.SemiBold);

        context.DrawText(labelText, new Point(group.Bounds.X + 8, group.Bounds.Y + 4));
    }

    private void DrawNode(DrawingContext context, NodeLayout node)
    {
        bool isSelected = node.ReplicaServerName == SelectedReplica;

        var bgBrush = new SolidColorBrush(isSelected ? Color.Parse("#3A3A5C") : Color.Parse("#2D2D3D"));
        var borderBrush = new SolidColorBrush(isSelected ? Color.Parse("#7B7BFF") : Color.Parse("#555570"));
        var borderPen = new Pen(borderBrush, isSelected ? 2.5 : 1.5);

        context.DrawRectangle(bgBrush, borderPen, new RoundedRect(node.Bounds, 8));

        // Server name
        var nameText = CreateFormattedText(
            TruncateServerName(node.ReplicaServerName),
            13,
            Brushes.White,
            FontWeight.SemiBold);
        context.DrawText(nameText, new Point(node.Bounds.X + 10, node.Bounds.Y + 10));

        // Role badge
        string roleLabel = node.Replica?.Role.ToString().ToUpperInvariant() ?? "UNKNOWN";
        var roleColor = node.IsPrimary
            ? new SolidColorBrush(GreenColor)
            : new SolidColorBrush(Color.Parse("#64B5F6"));
        var roleText = CreateFormattedText(roleLabel, 10, roleColor, FontWeight.Bold);
        context.DrawText(roleText, new Point(node.Bounds.X + 10, node.Bounds.Y + 32));

        // Database count
        if (node.Replica != null)
        {
            var dbText = CreateFormattedText(
                $"{node.Replica.DatabaseCount} databases",
                11,
                new SolidColorBrush(Color.Parse("#AAAACC")));
            context.DrawText(dbText, new Point(node.Bounds.X + 10, node.Bounds.Y + 52));
        }

        // Health indicator dot
        if (node.Replica != null)
        {
            var healthColor = node.Replica.SynchronizationHealth switch
            {
                SynchronizationHealth.Healthy => GreenColor,
                SynchronizationHealth.PartiallyHealthy => OrangeColor,
                SynchronizationHealth.NotHealthy => RedColor,
                _ => GrayColor
            };
            var dotCenter = new Point(node.Bounds.Right - 16, node.Bounds.Y + 16);
            context.DrawEllipse(new SolidColorBrush(healthColor), null, dotCenter, ParticleRadius * 2, ParticleRadius * 2);
        }

        // Connection state / availability mode
        if (node.Replica?.ConnectedState == ConnectedState.Disconnected)
        {
            var discText = CreateFormattedText("DISCONNECTED", 9, new SolidColorBrush(RedColor), FontWeight.Bold);
            context.DrawText(discText, new Point(node.Bounds.X + 10, node.Bounds.Y + 72));
        }
        else if (node.Replica != null)
        {
            string modeLabel = node.Replica.AvailabilityMode == AvailabilityMode.SynchronousCommit ? "SYNC" : "ASYNC";
            var modeText = CreateFormattedText(modeLabel, 10, new SolidColorBrush(Color.Parse("#888899")));
            context.DrawText(modeText, new Point(node.Bounds.X + 10, node.Bounds.Y + 72));
        }

        // Mini health dots for each database
        if (node.Replica != null && node.Replica.DatabaseStates.Count > 0)
        {
            double dotX = node.Bounds.X + 10;
            double dotY = node.Bounds.Y + NodeHeight - 16;
            int maxDots = Math.Min(node.Replica.DatabaseStates.Count, 12);

            for (int i = 0; i < maxDots; i++)
            {
                var dbState = node.Replica.DatabaseStates[i];
                var dbHealthLevel = HealthLevelExtensions.FromLsnDifference(dbState.LsnDifferenceFromPrimary);
                var dotColor = dbHealthLevel switch
                {
                    HealthLevel.InSync => GreenColor,
                    HealthLevel.SlightlyBehind => YellowColor,
                    HealthLevel.ModeratelyBehind => OrangeColor,
                    HealthLevel.DangerZone => RedColor,
                    _ => GrayColor
                };

                context.DrawEllipse(new SolidColorBrush(dotColor), null,
                    new Point(dotX + i * 12, dotY), 3, 3);
            }

            if (node.Replica.DatabaseStates.Count > maxDots)
            {
                var moreText = CreateFormattedText(
                    $"+{node.Replica.DatabaseStates.Count - maxDots}",
                    8,
                    new SolidColorBrush(Color.Parse("#888899")));
                context.DrawText(moreText, new Point(dotX + maxDots * 12, dotY - 4));
            }
        }
    }

    private static void DrawArrow(DrawingContext context, ArrowLayout arrow)
    {
        var color = GetHealthColor(arrow.HealthLevel);
        var brush = new SolidColorBrush(color);
        double thickness = arrow.IsDagLink ? 3 : 2;

        IPen pen;
        if (arrow.IsSynchronous)
        {
            pen = new Pen(brush, thickness);
        }
        else
        {
            pen = new Pen(brush, thickness, new DashStyle(new double[] { 8, 4 }, 0));
        }

        context.DrawLine(pen, arrow.From, arrow.To);

        // Arrowhead
        var dir = arrow.To - arrow.From;
        double len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
        if (len > 0)
        {
            double ux = dir.X / len;
            double uy = dir.Y / len;
            double px = -uy;
            double py = ux;
            double arrowSize = 10.0;

            var tip = arrow.To;
            var left = new Point(
                tip.X - ux * arrowSize + px * arrowSize * 0.4,
                tip.Y - uy * arrowSize + py * arrowSize * 0.4);
            var right = new Point(
                tip.X - ux * arrowSize - px * arrowSize * 0.4,
                tip.Y - uy * arrowSize - py * arrowSize * 0.4);

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(tip, true);
                ctx.LineTo(left);
                ctx.LineTo(right);
                ctx.EndFigure(true);
            }

            context.DrawGeometry(brush, null, geometry);
        }
    }

    private void DrawParticles(DrawingContext context, ArrowLayout arrow)
    {
        var color = GetHealthColor(arrow.HealthLevel);
        var particleBrush = new SolidColorBrush(color);
        const int particleCount = 3;

        for (int i = 0; i < particleCount; i++)
        {
            double t = (_animationTime + (double)i / particleCount) % 1.0;
            var pos = new Point(
                arrow.From.X + (arrow.To.X - arrow.From.X) * t,
                arrow.From.Y + (arrow.To.Y - arrow.From.Y) * t);

            context.DrawEllipse(particleBrush, null, pos, ParticleRadius, ParticleRadius);
        }
    }

    private static Color GetHealthColor(HealthLevel level) => level switch
    {
        HealthLevel.InSync => GreenColor,
        HealthLevel.SlightlyBehind => YellowColor,
        HealthLevel.ModeratelyBehind => OrangeColor,
        HealthLevel.DangerZone => RedColor,
        _ => GrayColor
    };

    private static FormattedText CreateFormattedText(
        string text, double fontSize, IBrush foreground, FontWeight weight = FontWeight.Normal)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Inter", FontStyle.Normal, weight),
            fontSize,
            foreground);
    }

    private static string TruncateServerName(string name)
    {
        // Show just the hostname if FQDN, preserving any SQL instance suffix
        int dotIndex = name.IndexOf('.');
        if (dotIndex > 0 && dotIndex < name.Length - 1)
        {
            int instanceSep = name.IndexOf('\\');
            if (instanceSep > dotIndex)
                return name[..dotIndex] + name[instanceSep..];

            return name[..dotIndex];
        }

        return name.Length > 24 ? name[..21] + "..." : name;
    }

    // Internal layout types
    private sealed class NodeLayout
    {
        public Rect Bounds { get; set; }
        public ReplicaInfo? Replica { get; set; }
        public string ReplicaServerName { get; set; } = string.Empty;
        public bool IsPrimary { get; set; }
    }

    private sealed class ArrowLayout
    {
        public Point From { get; set; }
        public Point To { get; set; }
        public bool IsSynchronous { get; set; }
        public HealthLevel HealthLevel { get; set; }
        public bool IsDagLink { get; set; }
    }

    private sealed class DagGroupLayout
    {
        public Rect Bounds { get; set; }
        public string MemberAgName { get; set; } = string.Empty;
        public ReplicaRole DistributedRole { get; set; }
    }
}
