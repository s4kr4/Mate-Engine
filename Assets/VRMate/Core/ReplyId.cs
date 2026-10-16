using System;

namespace VRMate
{
    /// <summary>
    /// Identifies one reply from the brain. A turn only identifies a reply within one connection (a restarted
    /// brain counts turns from the start again), so a reply is identified by the connection and the turn together.
    /// </summary>
    public struct ReplyId : IEquatable<ReplyId>
    {
        public ReplyId(long connection, long turn)
        {
            Connection = connection;
            Turn = turn;
        }

        /// <summary>Changes on every disconnect; a turn only identifies a reply within one connection.</summary>
        public long Connection { get; }

        public long Turn { get; }

        public static bool operator ==(ReplyId left, ReplyId right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(ReplyId left, ReplyId right)
        {
            return !left.Equals(right);
        }

        public bool Equals(ReplyId other)
        {
            return Connection == other.Connection && Turn == other.Turn;
        }

        public override bool Equals(object obj)
        {
            return obj is ReplyId other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Connection.GetHashCode() * 397) ^ Turn.GetHashCode();
            }
        }
    }
}
