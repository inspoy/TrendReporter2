namespace TrendReporter2.Core.Events;

public static class SecondaryMergeHardFilters
{
    public static bool ShouldExclude(EventMergeCandidate candidate, out string reason)
    {
        if (candidate.SourceEvent.Entities.Count > 0 &&
            candidate.TargetEvent.Entities.Count > 0 &&
            !HasIntersection(candidate.SourceEvent.Entities, candidate.TargetEvent.Entities))
        {
            reason = "实体不相交";
            return true;
        }

        if (!TimeWindowsOverlap(candidate.SourceEvent, candidate.TargetEvent) &&
            !string.Equals(candidate.SourceEvent.Type, candidate.TargetEvent.Type, StringComparison.OrdinalIgnoreCase))
        {
            reason = "时间窗口不重叠且事件类型不同";
            return true;
        }

        if (candidate.SourceEvent.Places.Count > 0 &&
            candidate.TargetEvent.Places.Count > 0 &&
            !HasIntersection(candidate.SourceEvent.Places, candidate.TargetEvent.Places))
        {
            reason = "地点冲突";
            return true;
        }

        if (IsNewsTopicMismatch(candidate.SourceEvent.Type, candidate.TargetEvent.Type))
        {
            reason = "NewsEvent 与 Topic 类型不兼容";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    public static bool IsNewsTopicMismatch(string leftType, string rightType)
        => (string.Equals(leftType, EventType.NewsEvent, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rightType, EventType.Topic, StringComparison.OrdinalIgnoreCase)) ||
           (string.Equals(leftType, EventType.Topic, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rightType, EventType.NewsEvent, StringComparison.OrdinalIgnoreCase));

    public static bool TimeWindowsOverlap(EventAggregate sourceEvent, EventAggregate targetEvent)
        => sourceEvent.FirstSeenAt <= targetEvent.LastSeenAt && targetEvent.FirstSeenAt <= sourceEvent.LastSeenAt;

    public static bool HasIntersection(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Any(value => right.Any(other => string.Equals(value, other, StringComparison.OrdinalIgnoreCase)));
}
