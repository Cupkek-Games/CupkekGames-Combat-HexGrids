using System.Collections.Generic;
using System.Text;
using CupkekGames.HexGrids;
using NUnit.Framework;
using UnityEngine;

namespace CupkekGames.Combat.HexGrids.Tests
{
    public class HexCombatBoardTests
    {
        // Pointy-top tiles one metre across (centre to centre), the field's (0, 0) at the world origin.
        private static HexCombatBoard Board(int columns = 8, int rows = 4, int stepTicks = 2)
        {
            var layout = new HexLayout(HexOrientation.PointyTop, 1f / Mathf.Sqrt(3f), Vector3.zero);
            return new HexCombatBoard(new HexField(columns, rows), layout, stepTicks);
        }

        private static HexCoord Tile(int column, int row) => HexField.FromOffset(column, row);

        private static void Run(HexCombatBoard board, int ticks, params int[] ids)
        {
            for (int t = 0; t < ticks; t++)
            {
                foreach (int id in ids) board.Tick(id);
            }
        }

        [Test]
        public void Place_TakesTheNearestFreeTile()
        {
            HexCombatBoard board = Board();
            Assert.AreEqual(Tile(3, 1), board.Place(1, Tile(3, 1)));
            HexCoord second = board.Place(2, Tile(3, 1));
            Assert.AreEqual(1, HexCoord.Distance(Tile(3, 1), second), "The next unit stands beside it.");
            Assert.AreEqual(second, PlaceTwice(), "The same calls pick the same tile.");
        }

        private static HexCoord PlaceTwice()
        {
            HexCombatBoard board = Board();
            board.Place(1, Tile(3, 1));
            return board.Place(2, Tile(3, 1));
        }

        [Test]
        public void AUnitFollows_OneTileAStep_UntilItsTargetIsInReach()
        {
            HexCombatBoard board = Board(stepTicks: 2);
            board.Place(1, Tile(0, 1));
            board.Place(2, Tile(6, 1));
            board.Follow(1, 2);

            Assert.IsTrue(board.Tick(1), "It sets off at once.");
            Assert.AreEqual(Tile(0, 1), board.TileOf(1), "It stands on its tile until the step completes.");
            Assert.IsTrue(board.Tick(1));
            Assert.AreEqual(Tile(0, 1), board.TileOf(1));
            Assert.IsTrue(board.Tick(1), "Arriving, it sets off for the next tile in the same tick.");
            Assert.AreEqual(Tile(1, 1), board.TileOf(1), "A step takes two ticks after it sets off.");

            Run(board, 20, 1);
            Assert.AreEqual(1, board.Distance(1, 2), "Melee walks up to the next tile and stops.");
            Assert.IsFalse(board.Tick(1));
        }

        [Test]
        public void TwoUnitsWalkingAtEachOther_MeetInTheMiddle_OnTheirLane()
        {
            // Fronts three tiles apart on one lane: each steps once and they meet, neither
            // side-stepping around the tile the other is leaving.
            HexCombatBoard board = Board(columns: 9, rows: 6, stepTicks: 2);
            board.Place(1, Tile(6, 2));
            board.Place(2, Tile(2, 2));
            board.Follow(1, 2);
            board.Follow(2, 1);
            Run(board, 12, 1, 2);
            Assert.AreEqual(1, board.Distance(1, 2), "They stand next to each other.");
            HexField.ToOffset(board.TileOf(1), out _, out int row1);
            HexField.ToOffset(board.TileOf(2), out _, out int row2);
            Assert.AreEqual(2, row1, "The first stays on its lane.");
            Assert.AreEqual(2, row2, "The second stays on its lane.");
        }

        [Test]
        public void IsFree_AndTryGetUnitAt_ReadWhoStandsWhere()
        {
            HexCombatBoard board = Board();
            board.Place(1, Tile(2, 1));
            board.Place(2, Tile(6, 1));
            board.Follow(1, 2);
            board.Tick(1);
            board.TryGetStep(1, out _, out HexCoord next, out _);

            Assert.IsFalse(board.IsFree(Tile(2, 1)), "Someone stands there.");
            Assert.IsTrue(board.TryGetUnitAt(Tile(2, 1), out int id));
            Assert.AreEqual(1, id);
            Assert.IsFalse(board.IsFree(next), "Reserved for a step.");
            Assert.IsFalse(board.TryGetUnitAt(next, out _), "Reserved, but nobody stands there yet.");
            Assert.IsTrue(board.IsFree(Tile(0, 3)));
            Assert.IsFalse(board.TryGetUnitAt(Tile(0, 3), out _));
            Assert.IsFalse(board.IsFree(Tile(20, 1)), "Off the field.");

            board.Field.Block(Tile(0, 3));
            Assert.IsFalse(board.IsFree(Tile(0, 3)), "Blocked.");
        }

        [Test]
        public void AReach_StopsThatManyTilesAway()
        {
            HexCombatBoard board = Board();
            board.Place(1, Tile(0, 1));
            board.Place(2, Tile(7, 1));
            board.SetReach(1, 4);
            board.Follow(1, 2);
            Run(board, 40, 1);
            Assert.AreEqual(4, board.Distance(1, 2));
        }

        [Test]
        public void StepsToReach_CountsTheWalk_FromWhereAUnitIsGoing_AroundWhoeverIsInTheWay()
        {
            HexCombatBoard board = Board(columns: 9, rows: 6, stepTicks: 2);
            board.Place(1, Tile(8, 2));
            board.Place(2, Tile(2, 2));
            Assert.AreEqual(5, board.StepsToReach(1, 2, 1), "Six tiles apart on a lane: five steps to stand beside it.");
            Assert.AreEqual(2, board.StepsToReach(1, 2, 4));
            Assert.AreEqual(0, board.StepsToReach(1, 2, 6), "Within reach: no steps.");

            board.Follow(1, 2);
            board.Tick(1);
            Assert.IsTrue(board.IsStepping(1));
            Assert.AreEqual(4, board.StepsToReach(1, 2, 1), "Mid-step it counts from the tile it is stepping to.");

            // A unit in the corner with both its neighbours taken: nobody can stand beside it.
            board.Place(3, Tile(0, 0));
            board.Place(4, Tile(1, 0));
            board.Place(5, Tile(0, 1));
            Assert.AreEqual(HexCombatBoard.Unreachable, board.StepsToReach(1, 3, 1));
            Assert.AreEqual(6, board.StepsToReach(1, 3, 2), "A longer reach gets there past them.");
            Assert.Throws<System.ArgumentOutOfRangeException>(() => board.StepsToReach(1, 2, 0));
        }

        [Test]
        public void HoldingOrRooted_TakesNoStep()
        {
            HexCombatBoard board = Board();
            board.Place(1, Tile(0, 1));
            board.Place(2, Tile(7, 1));
            board.Follow(1, 2);
            board.Hold(1);
            Assert.IsFalse(board.Tick(1));

            board.Follow(1, 2);
            board.SetRooted(1, true);
            Assert.IsFalse(board.Tick(1));

            board.SetRooted(1, false);
            Assert.IsTrue(board.Tick(1));
        }

        [Test]
        public void TwoUnits_NeverShareOrReserveOneTile()
        {
            HexCombatBoard board = Board();
            board.Place(1, Tile(0, 0));
            board.Place(2, Tile(0, 2));
            board.Place(3, Tile(5, 1));
            board.Follow(1, 3);
            board.Follow(2, 3);

            for (int t = 0; t < 30; t++)
            {
                board.Tick(1);
                board.Tick(2);
                Assert.AreNotEqual(board.TileOf(1), board.TileOf(2));
                board.TryGetStep(1, out _, out HexCoord to1, out _);
                board.TryGetStep(2, out _, out HexCoord to2, out _);
                if (board.IsStepping(1) && board.IsStepping(2)) Assert.AreNotEqual(to1, to2);
            }

            Assert.AreEqual(1, board.Distance(1, 3));
            Assert.AreEqual(1, board.Distance(2, 3));
        }

        [Test]
        public void Remove_FreesTheTile_AndItsFollowersStop()
        {
            HexCombatBoard board = Board();
            board.Place(1, Tile(0, 1));
            board.Place(2, Tile(6, 1));
            board.Follow(1, 2);
            board.Remove(2);
            Assert.IsFalse(board.Contains(2));
            Assert.IsFalse(board.Tick(1), "Its target fell: it stays.");
            Assert.AreEqual(Tile(6, 1), board.Place(3, Tile(6, 1)), "The tile is free again.");
        }

        [Test]
        public void Dash_SlidesAlongItsLine_AndStopsBeforeATakenTile()
        {
            HexCombatBoard board = Board();
            board.Place(1, Tile(0, 1));
            board.Place(2, Tile(4, 1));
            Vector3 start = board.Layout.ToWorld(Tile(0, 1));

            HexCoord end = board.Dash(1, start + Vector3.right * 6f);
            Assert.AreEqual(Tile(3, 1), end, "Three tiles on, the fourth is taken.");
            Assert.AreEqual(end, board.TileOf(1));
            Assert.AreEqual(Tile(0, 1), board.Place(3, Tile(0, 1)), "The tile it left is free.");
        }

        [Test]
        public void Dash_ThroughOthers_LandsOnTheFurthestFreeTile()
        {
            HexCombatBoard board = Board();
            board.Place(1, Tile(0, 1));
            board.Place(2, Tile(1, 1));
            Vector3 start = board.Layout.ToWorld(Tile(0, 1));
            Vector3 twoOn = start + Vector3.right * 2f * board.Layout.Spacing;

            Assert.AreEqual(Tile(0, 1), board.Dash(1, twoOn), "Pushed, it stops before the unit beside it.");
            Assert.AreEqual(Tile(2, 1), board.Dash(1, twoOn, passOver: true), "Through, it lands past it.");
            Assert.AreEqual(Tile(2, 1), board.TileOf(1));

            board.Place(3, Tile(3, 1));
            board.Place(4, Tile(4, 1));
            Vector3 fromHere = board.Layout.ToWorld(Tile(2, 1)) + Vector3.right * 2f * board.Layout.Spacing;
            Assert.AreEqual(Tile(2, 1), board.Dash(1, fromHere, passOver: true), "Nothing free on its way: it stays.");
        }

        [Test]
        public void Circles_TakeTheTilesWhoseCentreLiesInside()
        {
            HexCombatBoard board = Board(columns: 9, rows: 6);
            var tiles = new List<HexCoord>();
            foreach ((float radius, int count) in new[] { (0.5f, 1), (1f, 7), (1.6f, 7), (2.4f, 19) })
            {
                board.CircleTiles(Tile(4, 2), radius, tiles);
                Assert.AreEqual(count, tiles.Count, $"radius {radius}");
            }

            board.CircleTiles(Tile(0, 0), 1.6f, tiles);
            Assert.AreEqual(3, tiles.Count, "Only the field's tiles: a corner and its two neighbours.");
        }

        [Test]
        public void ArcsAndLines_AimAtATile()
        {
            HexCombatBoard board = Board(columns: 9, rows: 6);
            var tiles = new List<HexCoord>();

            board.ArcTiles(Tile(4, 2), Tile(6, 2), 2f, 120f, tiles);
            CollectionAssert.AreEquivalent(new[] { Tile(4, 3), Tile(5, 4), Tile(4, 1), Tile(5, 2), Tile(5, 3), Tile(5, 0), Tile(5, 1), Tile(6, 2) }, tiles,
                "A 120 degree arc of radius 2: 3 tiles on the first ring, 5 on the second.");
            board.ArcTiles(Tile(4, 2), Tile(4, 2), 2f, 120f, tiles);
            CollectionAssert.IsEmpty(tiles, "No way to aim.");

            board.LineTiles(Tile(2, 2), Tile(3, 2), 5, tiles);
            CollectionAssert.AreEqual(new[] { Tile(3, 2), Tile(4, 2), Tile(5, 2), Tile(6, 2), Tile(7, 2) }, tiles,
                "Five tiles from the one beside it, on past it.");
            // Off a hex axis: five tiles, each a step further out, through the target's tile.
            board.LineTiles(Tile(5, 2), Tile(3, 3), 5, tiles);
            Assert.AreEqual(5, tiles.Count);
            for (int i = 0; i < tiles.Count; i++) Assert.AreEqual(i + 1, HexCoord.Distance(Tile(5, 2), tiles[i]));
            CollectionAssert.Contains(tiles, Tile(3, 3));
            board.LineTiles(Tile(7, 2), Tile(6, 2), 5, tiles);
            CollectionAssert.AreEqual(new[] { Tile(6, 2), Tile(5, 2), Tile(4, 2), Tile(3, 2), Tile(2, 2) }, tiles);
        }

        [Test]
        public void CollectOn_TakesWhoStandsOnTheTiles_InIdOrder()
        {
            HexCombatBoard board = Board();
            board.Place(5, Tile(4, 1));
            board.Place(2, Tile(2, 1));
            board.Place(3, Tile(3, 1));
            board.Place(4, Tile(3, 3));
            var tiles = new List<HexCoord>();
            var ids = new List<int>();

            board.CircleTiles(Tile(3, 1), 1f, tiles);
            board.CollectOn(tiles, ids);
            CollectionAssert.AreEqual(new[] { 2, 3, 5 }, ids, "Within a tile of (3, 1): both neighbours on the row and itself.");

            board.LineTiles(Tile(2, 1), Tile(3, 1), 1, tiles);
            board.CollectOn(tiles, ids);
            CollectionAssert.AreEqual(new[] { 3 }, ids, "A line leaves its origin out.");
        }

        [Test]
        public void Hazards_AreWalkedAround_SteppedOff_AndFoughtOnWhenNothingElseServes()
        {
            HexCombatBoard board = Board(columns: 9, rows: 6, stepTicks: 2);
            board.Place(1, Tile(0, 2));
            board.Place(2, Tile(3, 2));
            var edge = new List<HexCoord>();
            for (int row = 0; row < 6; row++) edge.Add(Tile(0, row));
            board.SetHazards(edge);
            Assert.IsTrue(board.IsHazard(Tile(0, 2)));

            // In reach on the thorns: it steps off to a safe tile still in reach.
            board.SetReach(1, 4);
            board.Follow(1, 2);
            Assert.IsTrue(board.WouldStep(1), "In reach, but on thorns with a safe tile in reach: about to step off.");
            Assert.IsTrue(board.Tick(1), "It steps off.");
            Run(board, 10, 1);
            Assert.IsFalse(board.IsHazard(board.TileOf(1)));
            Assert.LessOrEqual(board.Distance(1, 2), 4);
            Assert.AreEqual(0, board.StepsToReach(1, 2, 4));

            // A wall of thorns across its row: it walks around, never onto them.
            board.SetHazards(new[] { Tile(5, 2), Tile(5, 1), Tile(5, 3) });
            board.Place(3, Tile(8, 2));
            board.SetReach(1, 1);
            board.Follow(1, 3);
            for (int t = 0; t < 60; t++)
            {
                board.Tick(1);
                Assert.IsFalse(board.IsHazard(board.TileOf(1)), $"On thorns at {board.TileOf(1)}.");
            }

            Assert.AreEqual(1, board.Distance(1, 3), "It got there.");

            // The whole field is thorns: it fights where it stands.
            board.SetHazards(board.Field.Tiles);
            Assert.IsFalse(board.WouldStep(1));
            Assert.IsFalse(board.Tick(1));
            Assert.AreEqual(0, board.StepsToReach(1, 3, 1));
        }

        [Test]
        public void TheSameCalls_PlayOutTheSame()
        {
            string Play()
            {
                HexCombatBoard board = Board(stepTicks: 3);
                for (int i = 0; i < 4; i++) board.Place(1 + i, Tile(1, i));
                for (int i = 0; i < 4; i++) board.Place(5 + i, Tile(6, i));
                for (int i = 0; i < 4; i++)
                {
                    board.Follow(1 + i, 5 + (i * 3) % 4);
                    board.Follow(5 + i, 1 + i);
                }

                var log = new StringBuilder();
                for (int t = 0; t < 40; t++)
                {
                    for (int id = 1; id <= 8; id++) board.Tick(id);
                    for (int id = 1; id <= 8; id++) log.Append(board.TileOf(id));
                }

                return log.ToString();
            }

            Assert.AreEqual(Play(), Play());
        }

        [Test]
        public void APush_MovesStraightAway_AndStopsBeforeWhatItCannotTake()
        {
            HexCombatBoard board = Board(columns: 8, rows: 4);
            board.Place(1, Tile(2, 1));
            board.Place(2, Tile(3, 1));

            Assert.AreEqual(2, board.Push(2, board.TileOf(1), 2));
            Assert.AreEqual(Tile(5, 1), board.TileOf(2), "Two tiles straight away along its row.");
            Assert.IsTrue(board.IsFree(Tile(3, 1)), "Its old tile is free.");

            board.Place(3, Tile(7, 1));
            Assert.AreEqual(1, board.Push(2, board.TileOf(1), 3), "It stops before the unit behind it.");
            Assert.AreEqual(Tile(6, 1), board.TileOf(2));

            board.Field.Block(Tile(4, 2));
            board.Place(4, Tile(3, 2));
            Assert.AreEqual(0, board.Push(4, Tile(2, 2), 2), "A wall right behind it holds it.");
            Assert.AreEqual(0, board.Push(1, board.TileOf(1), 2), "No way to push from its own tile.");

            board.Place(5, Tile(6, 3));
            Assert.AreEqual(1, board.Push(5, Tile(5, 3), 3), "The edge of the field stops it.");
            Assert.AreEqual(Tile(7, 3), board.TileOf(5));
        }

        [Test]
        public void TheTilesAround_AreTheTileAndItsRings_OnTheField()
        {
            HexCombatBoard board = Board(columns: 8, rows: 4);
            var tiles = new List<HexCoord>();

            board.AroundTiles(Tile(3, 1), 1, tiles);
            Assert.AreEqual(7, tiles.Count, "The tile and its six neighbours.");
            Assert.Contains(Tile(3, 1), tiles);

            board.AroundTiles(Tile(0, 0), 1, tiles);
            Assert.Less(tiles.Count, 7, "A corner keeps only the field's tiles.");

            board.AroundTiles(Tile(3, 1), 0, tiles);
            CollectionAssert.AreEqual(new[] { Tile(3, 1) }, tiles);
        }
    }
}
