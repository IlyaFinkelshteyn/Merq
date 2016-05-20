﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using Merq.Properties;

namespace Merq
{
	/// <summary>
	/// Provides the implementation for a reactive extensions event stream,
	/// allowing trending and analysis queries to be performed in real-time
	/// over the events pushed through the stream, as well as loosly coupled
	/// communication across components.
	/// </summary>
	public class EventStream : IEventStream
	{
		// All subjects active in the event stream.
		readonly ConcurrentDictionary<TypeInfo, SubjectWrapper> subjects = new ConcurrentDictionary<TypeInfo, SubjectWrapper>();
		// An cache of subjects indexed by the compatible event types, used to quickly lookup the subjects to 
		// invoke in a Push. Refreshed whenever a new Of<T> subscription is added.
		readonly ConcurrentDictionary<TypeInfo, SubjectWrapper[]> compatibleSubjects = new ConcurrentDictionary<TypeInfo, SubjectWrapper[]>();
		// Externally-produced events by IObservable<T> implementations.
		readonly HashSet<object> observables;

		/// <summary>
		/// Initializes the event stream.
		/// </summary>
		public EventStream ()
			: this(Enumerable.Empty<object>())
		{
		}

		/// <summary>
		/// Initializes the event stream with an optional list of 
		/// externally managed event producers which implement 
		/// <see cref="IObservable{T}"/>.
		/// </summary>
		public EventStream (params object[] observables)
			: this((IEnumerable<object>)observables)
		{
		}

		/// <summary>
		/// Initializes the event stream with an optional list of 
		/// externally managed event producers.
		/// </summary>
		public EventStream (IEnumerable<object> observables)
		{
			this.observables = new HashSet<object> (observables);
		}

		/// <summary>
		/// Pushes an event to the stream, causing any  subscriber to be invoked if appropriate.
		/// </summary>
		public virtual void Push<TEvent>(TEvent @event)
		{
			if (@event == null) throw new ArgumentNullException (nameof (@event));
			if (!IsValid<TEvent> ())
				throw new NotSupportedException (Strings.EventStream.PublishedEventNotPublic);

			if (observables.OfType<IObservable<TEvent>> ().Any ())
				throw new NotSupportedException (Strings.EventStream.PushNotSupportedForExternalEvent (typeof (TEvent).Name));

			var eventType = @event.GetType().GetTypeInfo();

			InvokeCompatible (@eventType, @event);
		}

		/// <summary>
		/// Observes the events of a given type <typeparamref name="TEvent"/>.
		/// </summary>
		public virtual IObservable<TEvent> Of<TEvent>()
		{
			if (!IsValid<TEvent> ())
				throw new NotSupportedException (Strings.EventStream.SubscribedEventNotPublic);

			var subject = (IObservable<TEvent>)subjects.GetOrAdd (typeof (TEvent).GetTypeInfo(), info => {
				// If we're creating a new subject, we need to clear the cache of compatible subjects
				compatibleSubjects.Clear ();
				return new SubjectWrapper<TEvent> ();
			});

			// Merge with any externally-produced observables that are compatible
			var compatibleObservables = new [] { subject }.Concat(observables.OfType<IObservable<TEvent>>()).ToArray();
			if (compatibleObservables.Length == 1)
				return compatibleObservables[0];

			return Observable.Merge (compatibleObservables);
		}

		void InvokeCompatible (TypeInfo info, object @event)
		{
			// We will call all subjects that are compatible with
			// the event type, not just concrete event type subscribers.
			var compatible = compatibleSubjects.GetOrAdd(info, eventType => subjects.Keys
				.Where(subjectEventType => subjectEventType.IsAssignableFrom(eventType))
				.Select(subjectEventType => subjects[subjectEventType])
				.ToArray());

			foreach (var subject in compatible) {
				subject.OnNext (@event);
			}
		}

		static bool IsValid<TEvent> () => typeof (TEvent).GetTypeInfo().IsPublic || typeof (TEvent).GetTypeInfo().IsNestedPublic;

		abstract class SubjectWrapper
		{
			public abstract void OnNext (object value);
		}

		class SubjectWrapper<T> : SubjectWrapper, ISubject<T>
		{
			ISubject<T> subject = new Subject<T>();

			public override void OnNext (object value)
			{
				subject.OnNext ((T)value);
			}

			void IObserver<T>.OnCompleted ()
			{
				subject.OnCompleted ();
			}

			void IObserver<T>.OnError (Exception error)
			{
				subject.OnError (error);
			}

			void IObserver<T>.OnNext (T value)
			{
				subject.OnNext (value);
			}

			IDisposable IObservable<T>.Subscribe (IObserver<T> observer)
			{
				return subject.Subscribe (observer);
			}
		}
	}
}
