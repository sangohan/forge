﻿// The MIT License (MIT)
//
// Copyright (c) 2013 Jacob Dufault
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
// associated documentation files (the "Software"), to deal in the Software without restriction,
// including without limitation the rights to use, copy, modify, merge, publish, distribute,
// sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
// NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
// DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using Forge.Collections;
using Forge.Entities.Implementation.Content;
using Forge.Entities.Implementation.Shared;
using Forge.Utilities;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Forge.Entities.Implementation.Runtime {
    /// <summary>
    /// The EntityManager requires an associated Entity which is not injected into the
    /// EntityManager.
    /// </summary>
    internal class GameEngine : MultithreadedSystemSharedContext, IGameEngine {
        private enum GameEngineNextState {
            SynchronizeState,
            Update
        }
        private GameEngineNextState _nextState;

        /// <summary>
        /// Should the EntityManager execute systems in separate threads?
        /// </summary>
        public static bool EnableMultithreading = true;

        /// <summary>
        /// The list of active Entities in the world.
        /// </summary>
        private UnorderedList<RuntimeEntity> _entities = new UnorderedList<RuntimeEntity>();

        /// <summary>
        /// A list of Entities that were added to the EntityManager in the last update loop. This
        /// means that they are now ready to actually be added to the EntityManager in this update.
        /// </summary>
        private List<RuntimeEntity> _addedEntities = new List<RuntimeEntity>();

        /// <summary>
        /// The entities which are added to the EntityManager in this frame. This is concurrently
        /// written to as systems create new entities.
        /// </summary>
        private ConcurrentWriterBag<RuntimeEntity> _notifiedAddingEntities = new ConcurrentWriterBag<RuntimeEntity>();

        /// <summary>
        /// A list of Entities that were removed from the EntityManager in the last update loop.
        /// This means that they are now ready to actually be removed from the EntityManager in this
        /// update.
        /// </summary>
        private List<RuntimeEntity> _removedEntities = new List<RuntimeEntity>();

        /// <summary>
        /// The entities which are removed to the EntityManager in this frame. This is concurrently
        /// written to as systems remove entities.
        /// </summary>
        private ConcurrentWriterBag<RuntimeEntity> _notifiedRemovedEntities = new ConcurrentWriterBag<RuntimeEntity>();

        /// <summary>
        /// A list of Entities that have been modified.
        /// </summary>
        /// <remarks>
        /// If you look at other local variables, this one does not follow a common pattern of also
        /// storing the previous update's results. This is because this data is not shared with
        /// systems.
        /// </remarks>
        private ConcurrentWriterBag<RuntimeEntity> _notifiedModifiedEntities = new ConcurrentWriterBag<RuntimeEntity>();

        /// <summary>
        /// Entities that have state changes. Entities can have state changes for multiple frames,
        /// so the entities inside of this list can be from any update before the current one.
        /// </summary>
        // TODO: convert this to a bag if performance is a problem
        private List<RuntimeEntity> _stateChangeEntities = new List<RuntimeEntity>();

        /// <summary>
        /// Entities which have state changes in this frame. This collection will be added to
        /// _stateChangeEntities during the next update.
        /// </summary>
        private ConcurrentWriterBag<RuntimeEntity> _notifiedStateChangeEntities = new ConcurrentWriterBag<RuntimeEntity>();

        /// <summary>
        /// All of the multithreaded systems.
        /// </summary>
        private List<MultithreadedSystem> _multithreadedSystems = new List<MultithreadedSystem>();

        /// <summary>
        /// Lock used when modifying _updateTask.
        /// </summary>
        private object _updateTaskLock = new object();

        /// <summary>
        /// The key we use to access unordered list metadata from the entity.
        /// </summary>
        private static int _entityUnorderedListMetadataKey = EntityManagerMetadata.GetUnorderedListMetadataIndex();

        private List<BaseSystem> _systems;

        /// <summary>
        /// Events that the EntityManager dispatches.
        /// </summary>
        public EventNotifier EventNotifier = new EventNotifier();

        IEventNotifier IGameEngine.EventNotifier {
            get {
                return EventNotifier;
            }
        }

        /// <summary>
        /// Singleton entity that contains global data.
        /// </summary>
        public RuntimeEntity SingletonEntity {
            get;
            set;
        }

        /// <summary>
        /// Gets the update number.
        /// </summary>
        /// <value>The update number.</value>
        public int UpdateNumber {
            get;
            private set;
        }

        /// <summary>
        /// ITemplateGroup JSON so that we can create a snapshot of the content.
        /// </summary>
        private string _templateJson;

        public GameEngine(string snapshotJson, string templateJson) {
            _templateJson = templateJson;

            // Create our own little island of references with its own set of templates
            GameSnapshot snapshot = GameSnapshotRestorer.Restore(snapshotJson, templateJson,
                Maybe.Just(this));

            _systems = snapshot.Systems;

            SystemDoneEvent = new CountdownEvent(0);

            // TODO: ensure that when correctly restore UpdateNumber
            //UpdateNumber = updateNumber;

            SingletonEntity = (RuntimeEntity)snapshot.SingletonEntity;

            Log<GameEngine>.Info("Submitting singleton entity EntityAddedEvent");
            EventNotifier.Submit(EntityAddedEvent.Create(SingletonEntity));

            foreach (var entity in snapshot.AddedEntities) {
                AddEntity((RuntimeEntity)entity);
            }

            foreach (var entity in snapshot.ActiveEntities) {
                RuntimeEntity runtimeEntity = (RuntimeEntity)entity;

                // add the entity
                InternalAddEntity(runtimeEntity);

                // TODO: verify that if the modification notifier is already triggered, we can
                // ignore this
                //if (((ContentEntity)entity).HasModification) {
                //    runtimeEntity.ModificationNotifier.Notify();
                //}

                // done via InternalAddEntity
                //if (deserializedEntity.HasStateChange) {
                //    deserializedEntity.Entity.DataStateChangeNotifier.Notify();
                //}
            }

            foreach (var entity in snapshot.RemovedEntities) {
                RuntimeEntity runtimeEntity = (RuntimeEntity)entity;

                // add the entity
                InternalAddEntity(runtimeEntity);

                // TODO: verify that if the modification notifier is already triggered, we can
                // ignore this
                //if (((ContentEntity)entity).HasModification) {
                //    runtimeEntity.ModificationNotifier.Notify();
                //}

                // done via InternalAddEntity
                //if (deserializedEntity.HasStateChange) {
                //    deserializedEntity.Entity.DataStateChangeNotifier.Notify();
                //}

                RemoveEntity(runtimeEntity);
            }

            foreach (var system in snapshot.Systems) {
                AddSystem(system);
            }

            _nextState = GameEngineNextState.SynchronizeState;
        }

        /// <summary>
        /// Registers the given system with the EntityManager.
        /// </summary>
        private void AddSystem(BaseSystem baseSystem) {
            if (baseSystem.EventDispatcher != null) {
                throw new InvalidOperationException("System already has an event " +
                    "dispatcher; either it got deserialized (it should not have), or the system " +
                    "was already registered with another game engine (which means the reference " +
                    "isolation is broken)");
            }
            baseSystem.EventDispatcher = EventNotifier;

            if (baseSystem is ITriggerFilterProvider) {
                MultithreadedSystem multithreadingSystem = new MultithreadedSystem(this, (ITriggerFilterProvider)baseSystem);
                foreach (var entity in _entities) {
                    multithreadingSystem.Restore(entity);
                }

                _multithreadedSystems.Add(multithreadingSystem);
            }
            else {
                throw new NotImplementedException();
            }
        }

        /// <summary>
        /// Internal method to add an entity to the entity manager and register it with all
        /// associated systems. This executes the add immediately.
        /// </summary>
        /// <param name="toAdd">The entity to add.</param>
        private void InternalAddEntity(RuntimeEntity toAdd) {
            toAdd.GameEngine = this;

            // notify listeners that we both added and created the entity
            Log<GameEngine>.Info("Submitting internal EntityAddedEvent for " + toAdd);
            EventNotifier.Submit(EntityAddedEvent.Create(toAdd));
            EventNotifier.Submit(ShowEntityEvent.Create(toAdd));

            // register listeners
            toAdd.ModificationNotifier.Listener += OnEntityModified;
            toAdd.DataStateChangeNotifier.Listener += OnEntityDataStateChanged;

            // notify ourselves of data state changes so that it the entity is pushed to systems
            toAdd.DataStateChangeNotifier.Notify();

            // ensure it contains metadata for our keys
            toAdd.Metadata.UnorderedListMetadata[_entityUnorderedListMetadataKey] = new UnorderedListMetadata();

            // add it our list of entities
            _entities.Add(toAdd, GetEntitiesListFromMetadata(toAdd));
        }

        private void SinglethreadFrameEnd() {
            _addedEntities.Clear();
            _removedEntities.Clear();
        }

        public void UpdateEntitiesWithStateChanges() {
            // _addedEntities and _removedEntities were cleared in SinglethreadFrameEnd()
            _notifiedAddingEntities.CopyIntoAndClear(_addedEntities);
            _notifiedRemovedEntities.CopyIntoAndClear(_removedEntities);

            ++UpdateNumber;

            // Add entities
            for (int i = 0; i < _addedEntities.Count; ++i) {
                RuntimeEntity toAdd = _addedEntities[i];

                InternalAddEntity(toAdd);

                // apply initialization changes
                toAdd.ApplyModifications();
            }
            // can't clear b/c it is shared

            // copy our state change entities notice that we do this after adding entities, because
            // adding entities triggers the data state change notifier
            _notifiedStateChangeEntities.CopyIntoAndClear(_stateChangeEntities);

            // Remove entities
            for (int i = 0; i < _removedEntities.Count; ++i) {
                RuntimeEntity toRemove = _removedEntities[i];

                // remove listeners
                toRemove.ModificationNotifier.Listener -= OnEntityModified;
                toRemove.DataStateChangeNotifier.Listener -= OnEntityDataStateChanged;

                // remove all data from the entity and then push said changes out
                foreach (DataAccessor accessor in toRemove.SelectData()) {
                    toRemove.RemoveData(accessor);
                }
                toRemove.DataStateChangeUpdate();

                // remove the entity from the list of entities
                _entities.Remove(toRemove, GetEntitiesListFromMetadata(toRemove));
                EventNotifier.Submit(DestroyedEntityEvent.Create(toRemove));

                // notify listeners we removed an event
                EventNotifier.Submit(EntityRemovedEvent.Create(toRemove));
            }
            // can't clear b/c it is shared

            // Do a data state change on the given items.
            {
                int i = 0;
                while (i < _stateChangeEntities.Count) {
                    if (_stateChangeEntities[i].NeedsMoreDataStateChangeUpdates() == false) {
                        // reset the notifier so it can be added to the _stateChangeEntities again
                        _stateChangeEntities[i].DataStateChangeNotifier.Reset();
                        _stateChangeEntities.RemoveAt(i);
                    }
                    else {
                        _stateChangeEntities[i].DataStateChangeUpdate();
                        ++i;
                    }
                }
            }

            // apply the modifications to the modified entities this data is not shared, so we can
            // clear it
            _notifiedModifiedEntities.IterateAndClear(modified => {
                modified.ApplyModifications();
            });

            // update the singleton data
            SingletonEntity.ApplyModifications();
            SingletonEntity.DataStateChangeUpdate();
        }

        private void MultithreadRunSystems(List<IGameInput> input) {
            // run all bookkeeping
            {
                SystemDoneEvent.Reset(_multithreadedSystems.Count);

                // run all systems
                for (int i = 0; i < _multithreadedSystems.Count; ++i) {
                    if (EnableMultithreading) {
                        Task.Factory.StartNew(_multithreadedSystems[i].BookkeepingBeforeRunningSystems);
                        //bool success = ThreadPool.UnsafeQueueUserWorkItem(_multithreadedSystems[i].BookkeepingAfterAllSystemsHaveRun, null);
                        //Contract.Requires(success, "Unable to submit threading task to ThreadPool");
                    }
                    else {
                        _multithreadedSystems[i].BookkeepingBeforeRunningSystems();
                    }
                }

                // block until the systems are done
                SystemDoneEvent.Wait();
            }

            // run all systems
            {
                SystemDoneEvent.Reset(_multithreadedSystems.Count);

                for (int i = 0; i < _multithreadedSystems.Count; ++i) {
                    if (EnableMultithreading) {
                        Task.Factory.StartNew(_multithreadedSystems[i].RunSystem, input);
                        //bool success = ThreadPool.UnsafeQueueUserWorkItem(_multithreadedSystems[i].RunSystem, input);
                        //Contract.Requires(success, "Unable to submit threading task to ThreadPool");
                    }
                    else {
                        _multithreadedSystems[i].RunSystem(input);
                    }
                }

                // block until the systems are done
                SystemDoneEvent.Wait();
            }
        }

        public void RunUpdateWorld(object commandsObject) {
            List<IGameInput> commands = (List<IGameInput>)commandsObject;

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            long frameBegin = stopwatch.ElapsedTicks;

            MultithreadRunSystems(commands);
            long multithreadEnd = stopwatch.ElapsedTicks;

            SinglethreadFrameEnd();

            stopwatch.Stop();

            StringBuilder builder = new StringBuilder();
            builder.AppendLine();

            builder.AppendFormat("Frame updating took {0} ticks (before {1}, concurrent {2})", stopwatch.ElapsedTicks, frameBegin, multithreadEnd - frameBegin);

            for (int i = 0; i < _multithreadedSystems.Count; ++i) {
                builder.AppendLine();
                builder.AppendFormat(@"  {1}/{2} ({3}|{4}|{5}|{6}|{7}) ticks for system {0}",
                    _multithreadedSystems[i].Trigger.GetType(),

                    _multithreadedSystems[i].PerformanceData.BookkeepingTicks,
                    _multithreadedSystems[i].PerformanceData.RunSystemTicks,

                    _multithreadedSystems[i].PerformanceData.AddedTicks,
                    _multithreadedSystems[i].PerformanceData.RemovedTicks,
                    _multithreadedSystems[i].PerformanceData.StateChangeTicks,
                    _multithreadedSystems[i].PerformanceData.ModificationTicks,
                    _multithreadedSystems[i].PerformanceData.UpdateTicks);

            }
            Log<GameEngine>.Info(builder.ToString());
        }

        private ManualResetEvent _updateWaitHandle = new ManualResetEvent(false);

        public WaitHandle Update(IEnumerable<IGameInput> input) {
            if (_nextState != GameEngineNextState.Update) {
                throw new InvalidOperationException("Invalid call to Update; was expecting " + _nextState);
            }

            if (_updateWaitHandle.WaitOne(0)) {
                throw new InvalidOperationException("Cannot call UpdateWorld before the returned " +
                    "WaitHandle has completed");
            }

            Task updateTask = Task.Factory.StartNew(RunUpdateWorld, input);
            updateTask.ContinueWith(t => {
                _nextState = GameEngineNextState.SynchronizeState;
                _synchronizeStateWaitHandle.Reset();
                _updateWaitHandle.Set();
            });

            return _updateWaitHandle;
        }

        private ManualResetEvent _synchronizeStateWaitHandle = new ManualResetEvent(false);
        public WaitHandle SynchronizeState() {
            if (_nextState != GameEngineNextState.SynchronizeState) {
                throw new InvalidOperationException("Invalid call to SynchronizeState; was expecting " + _nextState);
            }

            if (_synchronizeStateWaitHandle.WaitOne(0)) {
                throw new InvalidOperationException("Cannot call SynchronizeState before the " +
                    "returned WaitHandle has completed");
            }

            Task.Factory.StartNew(() => {
                UpdateEntitiesWithStateChanges();
                _nextState = GameEngineNextState.Update;
                _updateWaitHandle.Reset();
                _synchronizeStateWaitHandle.Set();
            });

            return _synchronizeStateWaitHandle;
        }

        public void DispatchEvents() {
            EventNotifier.DispatchEvents();
        }

        /// <summary>
        /// Registers the given entity with the world.
        /// </summary>
        /// <param name="instance">The instance to add</param>
        internal void AddEntity(RuntimeEntity instance) {
            _notifiedAddingEntities.Add(instance);
            EventNotifier.Submit(HideEntityEvent.Create(instance));
        }

        /// <summary>
        /// Removes the given entity from the world.
        /// </summary>
        /// <param name="instance">The entity instance to remove</param>
        internal void RemoveEntity(RuntimeEntity instance) {
            _notifiedRemovedEntities.Add(instance);
            EventNotifier.Submit(HideEntityEvent.Create(instance));
        }

        /// <summary>
        /// Helper method that returns the _entities unordered list metadata.
        /// </summary>
        private UnorderedListMetadata GetEntitiesListFromMetadata(RuntimeEntity entity) {
            return entity.Metadata.UnorderedListMetadata[_entityUnorderedListMetadataKey];
        }

        /// <summary>
        /// Called when an Entity has been modified.
        /// </summary>
        private void OnEntityModified(RuntimeEntity sender) {
            _notifiedModifiedEntities.Add(sender);
        }

        /// <summary>
        /// Called when an entity has data state changes
        /// </summary>
        private void OnEntityDataStateChanged(RuntimeEntity sender) {
            _notifiedStateChangeEntities.Add(sender);
        }

        /// <summary>
        /// Returns all entities that will be added in the next update.
        /// </summary>
        private List<IEntity> GetEntitiesToAdd() {
            return _notifiedAddingEntities.ToList().Select(e => (IEntity)e).ToList();
        }

        /// <summary>
        /// Returns all entities that will be removed in the next update.
        /// </summary>
        private List<IEntity> GetEntitiesToRemove() {
            return _notifiedRemovedEntities.ToList().Select(e => (IEntity)e).ToList();
        }

        #region MultithreadedSystemSharedContext Implementation
        List<RuntimeEntity> MultithreadedSystemSharedContext.AddedEntities {
            get { return _addedEntities; }
        }

        List<RuntimeEntity> MultithreadedSystemSharedContext.RemovedEntities {
            get { return _removedEntities; }
        }

        List<RuntimeEntity> MultithreadedSystemSharedContext.StateChangedEntities {
            get { return _stateChangeEntities; }
        }

        /// <summary>
        /// Event the system uses to notify the primary thread that it is done processing.
        /// </summary>
        public CountdownEvent SystemDoneEvent { get; private set; }
        #endregion

        private GameSnapshot GetRawSnapshot() {
            GameSnapshot snapshot = new GameSnapshot();

            snapshot.SingletonEntity = SingletonEntity;

            foreach (var adding in _notifiedAddingEntities.ToList()) {
                snapshot.AddedEntities.Add(adding);
            }

            List<RuntimeEntity> removing = _notifiedRemovedEntities.ToList();
            foreach (var entity in _entities) {
                bool isRemoving = removing.Contains(entity);

                if (isRemoving) {
                    snapshot.RemovedEntities.Add(entity);
                }
                else {
                    snapshot.ActiveEntities.Add(entity);
                }

            }

            snapshot.Systems = _systems;

            return snapshot;
        }

        public IGameSnapshot TakeSnapshot() {
            string snapshotJson = SerializationHelpers.Serialize(GetRawSnapshot(),
                RequiredConverters.GetConverters(),
                RequiredConverters.GetContextObjects(Maybe<GameEngine>.Empty));

            return GameSnapshotRestorer.Restore(snapshotJson, _templateJson, Maybe<GameEngine>.Empty);
        }

        public int GetVerificationHash() {
            string json = SerializationHelpers.Serialize(GetRawSnapshot(),
                RequiredConverters.GetConverters(),
                RequiredConverters.GetContextObjects(Maybe<GameEngine>.Empty));
            return json.GetHashCode();
        }
    }
}