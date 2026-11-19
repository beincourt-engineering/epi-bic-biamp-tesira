using System.Collections.Generic;
using FluentAssertions;
using Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Queue;
using Xunit;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Tests;

/// <summary>The command queue's express lane: mute changes go ahead of everything waiting, each lane in order.</summary>
public class ExpressLaneQueueTests
{
    [Fact]
    public void Normal_items_come_out_in_order()
    {
        var q = new ExpressLaneQueue<string>(10);
        q.TryEnqueue("a", express: false);
        q.TryEnqueue("b", express: false);

        Take(q).Should().Equal("a", "b");
    }

    [Fact]
    public void Express_items_go_ahead_of_waiting_normal_items_and_keep_their_own_order()
    {
        var q = new ExpressLaneQueue<string>(10);
        q.TryEnqueue("preset 1", express: false);
        q.TryEnqueue("preset 2", express: false);
        q.TryEnqueue("mute 1", express: true);
        q.TryEnqueue("level", express: false);
        q.TryEnqueue("mute 2", express: true);

        Take(q).Should().Equal("mute 1", "mute 2", "preset 1", "preset 2", "level");
    }

    [Fact]
    public void An_express_item_added_later_still_goes_next()
    {
        var q = new ExpressLaneQueue<string>(10);
        q.TryEnqueue("preset 1", express: false);
        q.TryEnqueue("preset 2", express: false);

        q.TryDequeue(out var first).Should().BeTrue();
        first.Should().Be("preset 1");

        q.TryEnqueue("mute", express: true);
        Take(q).Should().Equal("mute", "preset 2");
    }

    [Fact]
    public void Count_and_empty_cover_both_lanes()
    {
        var q = new ExpressLaneQueue<string>(10);
        q.IsEmpty.Should().BeTrue();

        q.TryEnqueue("a", express: false);
        q.TryEnqueue("b", express: true);
        q.Count.Should().Be(2);
        q.IsEmpty.Should().BeFalse();

        q.Clear();
        q.IsEmpty.Should().BeTrue();
        q.TryDequeue(out _).Should().BeFalse();
    }

    [Fact]
    public void A_full_lane_refuses_and_the_other_lane_still_takes_items()
    {
        var q = new ExpressLaneQueue<string>(2);
        q.TryEnqueue("a", express: false).Should().BeTrue();
        q.TryEnqueue("b", express: false).Should().BeTrue();
        q.TryEnqueue("c", express: false).Should().BeFalse();

        q.TryEnqueue("mute", express: true).Should().BeTrue();
        Take(q).Should().Equal("mute", "a", "b");
    }

    [Fact]
    public void No_capacity_means_no_limit()
    {
        var q = new ExpressLaneQueue<int>(0);
        for (var i = 0; i < 1000; i++)
            q.TryEnqueue(i, express: false).Should().BeTrue();

        q.Count.Should().Be(1000);
    }

    private static List<string> Take(ExpressLaneQueue<string> q)
    {
        var items = new List<string>();
        while (q.TryDequeue(out var item))
            items.Add(item);
        return items;
    }
}
