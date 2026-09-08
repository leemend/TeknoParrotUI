using System;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace TeknoParrotUi.Common.InputListening
{
    /// <summary>
    /// Routes the current Golden Tee Options owner's trackball to the player
    /// whose menu is actually being consumed by the game.
    ///
    /// Discovery:
    /// - On the first owner movement, briefly fan the delta to the other player MMFs.
    /// - Golden Tee sets reset=1 only on the MMF it actually consumed.
    /// - A short probe identifies that consumer, clears the unused duplicate buffers,
    ///   and all subsequent owner movement is written ONLY to that consumer.
    ///
    /// This prevents duplicated trackball deltas from sitting in inactive player
    /// buffers and being consumed later when their turn begins.
    /// </summary>
    internal static class GoldenTeeOptionsTrackballBroadcast
    {
        private static readonly object Sync = new object();

        private static readonly string[] BufferNames =
        {
            null,
            "RawInputTrackballSharedMemory",
            "RawInputTrackballSharedMemory2",
            "RawInputTrackballSharedMemory3",
            "RawInputTrackballSharedMemory4"
        };

        private static readonly MemoryMappedFile[] Files = new MemoryMappedFile[5];
        private static readonly MemoryMappedViewAccessor[] Views = new MemoryMappedViewAccessor[5];
        private static readonly short[] CurrentX = new short[5];
        private static readonly short[] CurrentY = new short[5];

        private static int _ownerPlayer;
        private static int _consumerPlayer;
        private static int _probeAttempts;
        private static Timer _probeTimer;

        public static void BroadcastToOtherPlayers(int ownerPlayer, int deltaX, int deltaY)
        {
            if (ownerPlayer < 1 || ownerPlayer > 4)
                return;

            lock (Sync)
            {
                EnsureAllBuffers();

                if (_ownerPlayer != ownerPlayer)
                {
                    ResetSession(ownerPlayer);
                }

                // Once Golden Tee has told us which player's trackball MMF it is
                // actually consuming for this Options session, target only that MMF.
                if (_consumerPlayer >= 1 && _consumerPlayer <= 4 &&
                    _consumerPlayer != ownerPlayer)
                {
                    Accumulate(_consumerPlayer, deltaX, deltaY);
                    return;
                }

                // Discovery only: duplicate this initial movement to the other
                // player buffers. The probe below will identify which one the game
                // consumed and immediately clear the unused copies.
                for (int player = 1; player <= 4; player++)
                {
                    if (player == ownerPlayer)
                        continue;

                    Accumulate(player, deltaX, deltaY);
                }

                ArmConsumerProbe();
            }
        }

        public static int GetConsumerPlayer(int ownerPlayer)
        {
            if (ownerPlayer < 1 || ownerPlayer > 4)
                return 0;

            lock (Sync)
            {
                if (_ownerPlayer != ownerPlayer)
                    return 0;

                return _consumerPlayer;
            }
        }

        private static void ResetSession(int ownerPlayer)
        {
            _ownerPlayer = ownerPlayer;
            _consumerPlayer = 0;
            _probeAttempts = 0;

            _probeTimer?.Dispose();
            _probeTimer = null;

            // Remove any duplicated deltas left by a prior Options owner/session.
            // Do not touch the new owner's normal buffer.
            for (int player = 1; player <= 4; player++)
            {
                if (player != ownerPlayer)
                    ClearBuffer(player);
            }
        }

        private static void ArmConsumerProbe()
        {
            if (_probeTimer != null)
                return;

            _probeAttempts = 0;
            _probeTimer = new Timer(
                ProbeConsumer,
                null,
                30,
                Timeout.Infinite);
        }

        private static void ProbeConsumer(object state)
        {
            lock (Sync)
            {
                _probeTimer?.Dispose();
                _probeTimer = null;

                if (_ownerPlayer < 1 || _ownerPlayer > 4 || _consumerPlayer != 0)
                    return;

                int detected = 0;

                for (int player = 1; player <= 4; player++)
                {
                    if (player == _ownerPlayer)
                        continue;

                    EnsureBuffer(player);

                    if (Views[player].ReadInt32(8) == 1)
                    {
                        detected = player;
                        break;
                    }
                }

                if (detected != 0)
                {
                    _consumerPlayer = detected;

                    // The discovery movement was copied to every non-owner buffer.
                    // Keep only the buffer Golden Tee actually consumed/uses.
                    for (int player = 1; player <= 4; player++)
                    {
                        if (player == _ownerPlayer || player == _consumerPlayer)
                            continue;

                        ClearBuffer(player);
                    }

                    return;
                }

                // The game may not have consumed the first delta within 30 ms.
                // Retry briefly, then wait for the next physical movement.
                _probeAttempts++;
                if (_probeAttempts < 6)
                {
                    _probeTimer = new Timer(
                        ProbeConsumer,
                        null,
                        30,
                        Timeout.Infinite);
                }
            }
        }

        private static void EnsureAllBuffers()
        {
            for (int player = 1; player <= 4; player++)
                EnsureBuffer(player);
        }

        private static void EnsureBuffer(int player)
        {
            if (Views[player] != null)
                return;

            Files[player] = MemoryMappedFile.CreateOrOpen(BufferNames[player], 12);
            Views[player] = Files[player].CreateViewAccessor();
        }

        private static void ClearBuffer(int player)
        {
            EnsureBuffer(player);

            CurrentX[player] = 0;
            CurrentY[player] = 0;

            Views[player].Write(0, 0);
            Views[player].Write(4, 0);
            Views[player].Write(8, 0);
        }

        private static void Accumulate(int player, int deltaX, int deltaY)
        {
            EnsureBuffer(player);

            var accessor = Views[player];

            if (accessor.ReadInt32(8) == 1)
            {
                CurrentX[player] = 0;
                CurrentY[player] = 0;
                accessor.Write(8, 0);
            }

            int nextX = Math.Clamp(CurrentX[player] + deltaX, short.MinValue, short.MaxValue);
            int nextY = Math.Clamp(CurrentY[player] + deltaY, short.MinValue, short.MaxValue);

            CurrentX[player] = (short)nextX;
            CurrentY[player] = (short)nextY;

            accessor.Write(0, (int)CurrentX[player]);
            accessor.Write(4, (int)CurrentY[player]);
        }
    }
}