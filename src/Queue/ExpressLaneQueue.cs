using System.Collections.Generic;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Queue
{
    /// <summary>
    /// A first-in, first-out queue with an express lane. Items in the express lane are dequeued before any item in
    /// the normal lane; each lane keeps its own order. Used so that a mute command is not held behind preset recalls,
    /// which can take seconds each on the DSP. Not thread safe: <see cref="TesiraQueue"/> uses it under its lock.
    /// Free of Crestron types so it can be unit tested.
    /// </summary>
    public class ExpressLaneQueue<T>
    {
        private readonly Queue<T> _express = new Queue<T>();
        private readonly Queue<T> _normal = new Queue<T>();
        private readonly int _capacity;

        /// <param name="capacity">Most items each lane holds; 0 or less for no limit.</param>
        public ExpressLaneQueue(int capacity)
        {
            _capacity = capacity;
        }

        /// <summary>Items waiting in both lanes.</summary>
        public int Count => _express.Count + _normal.Count;

        /// <summary>Nothing waiting in either lane.</summary>
        public bool IsEmpty => Count == 0;

        /// <summary>Adds an item to the end of its lane. False when that lane is full; the item is not added.</summary>
        public bool TryEnqueue(T item, bool express)
        {
            var lane = express ? _express : _normal;
            if (_capacity > 0 && lane.Count >= _capacity)
                return false;

            lane.Enqueue(item);
            return true;
        }

        /// <summary>Takes the oldest express item, or else the oldest normal one. False when both lanes are empty.</summary>
        public bool TryDequeue(out T item)
        {
            if (_express.Count > 0)
            {
                item = _express.Dequeue();
                return true;
            }

            if (_normal.Count > 0)
            {
                item = _normal.Dequeue();
                return true;
            }

            item = default(T);
            return false;
        }

        /// <summary>Empties both lanes.</summary>
        public void Clear()
        {
            _express.Clear();
            _normal.Clear();
        }
    }
}
