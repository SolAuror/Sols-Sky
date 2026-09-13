using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace Sol.Environment.EditorTools
{
    sealed class ElementaWindDial : VisualElement
    {
        float _heading, _cloudHeading;
        public event Action<float> Changed;
        public ElementaWindDial()
        {
            AddToClassList("elementa-dial"); focusable = true;
            tooltip = "Wind heading: 0° = +X, 90° = +Z. Drag or use arrow keys; Shift adjusts 10°.";
            var north = new Label("+Z") { pickingMode = PickingMode.Ignore };
            north.style.alignSelf = Align.Center; Add(north);
            generateVisualContent += Draw;
            RegisterCallback<PointerDownEvent>(e => { if (e.button != 0) return; Focus(); this.CapturePointer(e.pointerId); SetFromPointer(e.localPosition); e.StopPropagation(); });
            RegisterCallback<PointerMoveEvent>(e => { if (this.HasPointerCapture(e.pointerId)) SetFromPointer(e.localPosition); });
            RegisterCallback<PointerUpEvent>(e => { if (this.HasPointerCapture(e.pointerId)) this.ReleasePointer(e.pointerId); });
            RegisterCallback<KeyDownEvent>(e =>
            {
                int sign = e.keyCode == KeyCode.LeftArrow || e.keyCode == KeyCode.DownArrow ? -1 : e.keyCode == KeyCode.RightArrow || e.keyCode == KeyCode.UpArrow ? 1 : 0;
                if (sign == 0) return;
                _heading = Mathf.Repeat(_heading + sign * (e.shiftKey ? 10 : 1), 360); Changed?.Invoke(_heading); MarkDirtyRepaint(); e.StopPropagation();
            });
        }
        public void SetHeadings(float heading, float cloud)
        {
            if (Mathf.Approximately(_heading, heading) && Mathf.Approximately(_cloudHeading, cloud)) return;
            _heading = heading; _cloudHeading = cloud; MarkDirtyRepaint();
        }
        void SetFromPointer(Vector2 position)
        {
            Vector2 delta = position - contentRect.center;
            _heading = Mathf.Repeat(Mathf.Atan2(-delta.y, delta.x) * Mathf.Rad2Deg, 360);
            Changed?.Invoke(_heading); MarkDirtyRepaint();
        }
        void Draw(MeshGenerationContext context)
        {
            var painter = context.painter2D;
            Vector2 center = contentRect.center;
            float radius = Mathf.Min(contentRect.width, contentRect.height) * .37f;
            painter.strokeColor = resolvedStyle.color; painter.lineWidth = 1;
            painter.BeginPath(); painter.Arc(center, radius, 0, 360); painter.Stroke();
            DrawHand(painter, center, radius * .8f, _cloudHeading, 1);
            DrawHand(painter, center, radius, _heading, 3);
        }
        void DrawHand(Painter2D painter, Vector2 center, float radius, float heading, float width)
        {
            painter.lineWidth = width; painter.BeginPath(); painter.MoveTo(center);
            painter.LineTo(center + new Vector2(Mathf.Cos(heading * Mathf.Deg2Rad), -Mathf.Sin(heading * Mathf.Deg2Rad)) * radius); painter.Stroke();
        }
    }

    sealed class ElementaTimeline : VisualElement
    {
        float _delay, _span, _progress = -1;
        readonly Color _color;
        public ElementaTimeline(Color color)
        {
            _color = color; AddToClassList("elementa-timeline"); generateVisualContent += Draw;
        }
        public void SetValues(float delay, float span, float progress)
        {
            if (_delay == delay && _span == span && _progress == progress) return;
            _delay = delay; _span = span; _progress = progress; MarkDirtyRepaint();
        }
        void Draw(MeshGenerationContext context)
        {
            var p = context.painter2D; var r = contentRect;
            p.strokeColor = new Color(.5f, .5f, .5f, .3f); p.lineWidth = 10;
            p.BeginPath(); p.MoveTo(new Vector2(0, r.height / 2)); p.LineTo(new Vector2(r.width, r.height / 2)); p.Stroke();
            p.strokeColor = _color;
            p.BeginPath(); p.MoveTo(new Vector2(r.width * Mathf.Clamp01(_delay), r.height / 2));
            p.LineTo(new Vector2(r.width * Mathf.Clamp01(_delay + _span), r.height / 2)); p.Stroke();
            if (_progress < 0) return;
            p.strokeColor = resolvedStyle.color; p.lineWidth = 2;
            p.BeginPath(); p.MoveTo(new Vector2(r.width * Mathf.Clamp01(_progress), 2));
            p.LineTo(new Vector2(r.width * Mathf.Clamp01(_progress), r.height - 2)); p.Stroke();
        }
    }
}
