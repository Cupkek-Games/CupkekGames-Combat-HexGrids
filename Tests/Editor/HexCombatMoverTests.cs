using System;
using System.Collections.Generic;
using System.Threading;
using CupkekGames.HexGrids;
using CupkekGames.TimeSystem;
using NUnit.Framework;
using PrimeTween;
using UnityEditor;
using UnityEngine;

namespace CupkekGames.Combat.HexGrids.Tests
{
    /// <summary>
    /// A unit's mover on a hex space, as the formation phase uses it: lifting a unit off
    /// the field frees its tile, setting it down lands exactly on a free tile at the height
    /// given, and a unit whose fight is running cannot be lifted.
    /// </summary>
    public class HexCombatMoverTests
    {
        private readonly List<UnityEngine.Object> _owned = new List<UnityEngine.Object>();
        private HexFieldView _field;
        private HexCombatSpace _space;
        private TimeManager _time;

        [SetUp]
        public void SetUp()
        {
            var root = new GameObject("HexCombatMoverTests");
            _owned.Add(root);
            _field = root.AddComponent<HexFieldView>();
            _space = root.AddComponent<HexCombatSpace>();
            var so = new SerializedObject(_space);
            so.FindProperty("_field").objectReferenceValue = _field;
            so.ApplyModifiedPropertiesWithoutUndo();
            _time = root.AddComponent<TimeManager>();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (UnityEngine.Object owned in _owned)
            {
                if (owned != null) UnityEngine.Object.DestroyImmediate(owned);
            }

            _owned.Clear();
        }

        private static HexCoord Tile(int column, int row) => HexField.FromOffset(column, row);

        private HexCombatMover Unit(HexCoord tile)
        {
            var go = new GameObject("Unit " + tile);
            _owned.Add(go);
            go.transform.position = _field.ToWorld(tile);
            return (HexCombatMover)_space.CreateMover(go.AddComponent<CombatUnitView>());
        }

        [Test]
        public void Lift_FreesItsTile_SetDown_StandsExactlyThere_AtTheHeightGiven()
        {
            HexCombatMover mover = Unit(Tile(1, 1));
            mover.Lift();
            Assert.IsFalse(mover.Placed);
            Assert.IsTrue(mover.Lifted);
            Assert.IsTrue(_space.Board.IsFree(Tile(1, 1)), "Its tile is free while it is carried.");

            mover.View.transform.position += Vector3.up * 0.15f;
            mover.SetDown(_field.ToWorld(Tile(5, 2)));
            Assert.IsTrue(mover.Placed);
            Assert.IsFalse(mover.Lifted);
            Assert.AreEqual(Tile(5, 2), _space.Board.TileOf(mover.Id));
            Vector3 centre = _field.ToWorld(Tile(5, 2));
            Assert.AreEqual(centre.x, mover.View.transform.position.x, 1e-4f);
            Assert.AreEqual(centre.z, mover.View.transform.position.z, 1e-4f);
            Assert.AreEqual(0f, mover.View.transform.position.y, 1e-4f, "It stands at the height it was set down at, not the carried one.");
        }

        [Test]
        public void SetDown_OnATakenOrMissingTile_Throws_AndChangesNothing()
        {
            HexCombatMover carried = Unit(Tile(1, 1));
            HexCombatMover other = Unit(Tile(3, 1));
            carried.Lift();

            Assert.Throws<InvalidOperationException>(() => carried.SetDown(_field.ToWorld(Tile(3, 1))), "Taken.");
            Assert.Throws<InvalidOperationException>(() => carried.SetDown(_field.ToWorld(Tile(30, 1))), "Off the field.");
            Assert.IsTrue(carried.Lifted, "Still carried.");
            Assert.AreEqual(Tile(3, 1), _space.Board.TileOf(other.Id), "The other unit stays put.");
            Assert.Throws<InvalidOperationException>(() => other.SetDown(_field.ToWorld(Tile(5, 1))), "Only a lifted unit is set down.");
        }

        [Test]
        public void TwoLifts_ThenTwoSetDowns_SwapTwoUnits()
        {
            HexCombatMover a = Unit(Tile(1, 1));
            HexCombatMover b = Unit(Tile(4, 2));
            a.Lift();
            b.Lift();
            a.SetDown(_field.ToWorld(Tile(4, 2)));
            b.SetDown(_field.ToWorld(Tile(1, 1)));
            Assert.AreEqual(Tile(4, 2), _space.Board.TileOf(a.Id));
            Assert.AreEqual(Tile(1, 1), _space.Board.TileOf(b.Id));
        }

        [Test]
        public void Lift_RefusesAUnitOffTheBoard_OrFighting_AndStart_RefusesALiftedUnit()
        {
            HexCombatMover mover = Unit(Tile(2, 2));
            mover.Start(new TimeBundle(_time));
            Assert.Throws<InvalidOperationException>(() => mover.Lift(), "Its fight is running.");

            mover.Stop();
            mover.Lift();
            Assert.Throws<InvalidOperationException>(() => mover.Lift(), "Already off the board.");
            Assert.Throws<InvalidOperationException>(() => mover.Start(new TimeBundle(_time)), "Set it down first.");
        }

        [Test]
        public void IsSettled_OnlyStandingOnItsTile()
        {
            HexCombatMover walker = Unit(Tile(0, 1));
            HexCombatMover target = Unit(Tile(6, 1));
            Assert.IsTrue(walker.IsSettled);

            _space.Board.Follow(walker.Id, target.Id);
            Assert.IsFalse(walker.IsSettled, "About to set off: its target is out of reach.");
            _space.Board.Tick(walker.Id);
            Assert.IsFalse(walker.IsSettled, "Walking.");
            _space.Board.TryGetStep(walker.Id, out _, out HexCoord next, out _);

            walker.Lift();
            Assert.IsFalse(walker.IsSettled, "Carried.");
            Assert.IsTrue(_space.Board.IsFree(next), "Lifting frees the tile it had reserved.");
        }

        [Test]
        public void ADash_TakesItsTileAtOnce_TheModelFollows()
        {
            HexCombatMover dasher = Unit(Tile(1, 1));
            Vector3 start = dasher.View.transform.position;
            dasher.Dash(Vector3.right * 2f, 0.5f, Ease.Linear, CombatDashMode.Push, CancellationToken.None);

            Assert.AreEqual(Tile(3, 1), _space.Board.TileOf(dasher.Id), "The board moves it two tiles at once.");
            Assert.IsTrue(dasher.Dashing);
            Assert.IsFalse(dasher.IsSettled, "Not settled while its model is on the way.");
            Assert.AreEqual(start, dasher.View.transform.position, "The model starts where it stood.");
        }

        [Test]
        public void ACosmeticDash_MovesOnlyTheModel()
        {
            HexCombatMover kicker = Unit(Tile(3, 1));
            kicker.Dash(Vector3.back * 0.8f, 0.5f, Ease.Linear, CombatDashMode.Cosmetic, CancellationToken.None);

            Assert.AreEqual(Tile(3, 1), _space.Board.TileOf(kicker.Id), "It keeps its tile.");
            Assert.IsTrue(kicker.Dashing, "Its model is on the way out and back.");
        }
    }
}
