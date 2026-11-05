# 05: Open/Closed filter and lights

**What to build:** The Backlog can be filtered on Open (default: every Intent not Done or Cancelled) and Closed (Done and Cancelled). Each Intent in the list, and on its detail page, shows its state and a light: green when agents are working, orange for Needs Attention, red for failed. States in which nothing runs (Draft, Approved, Paused, Done, Cancelled) show no light. The Backlog and detail page update live, over Blazor's SignalR connection, when an Intent changes state, without a reload. Covers user stories 37 and 38.

The light is derived by one function of the Intent Lifecycle. Today only green (Refining) is reachable. The orange conditions (Analyst waiting for answers, Spec awaiting approval, an agent's question, Review Comments awaiting triage) and red conditions (Failed Task, Sandbox creation failed, merge failed) are added to it by the tickets that introduce them. The Closed filter stays empty until Done or Cancel exist.

**Blocked by:** 04 (Start an Intent, with Active Intent slots)

**Status:** done

- [x] The Backlog offers Open and Closed filters; Open is the default and the choice is reflected in the URL
- [x] Each Intent shows its state and, where it has one, its light; a Refining Intent is green, a Draft has none
- [x] The light is derived in one place in the Intent Lifecycle, with a documented point where later tickets add orange and red conditions
- [x] The light is distinguishable without colour (label or icon), so it doesn't rely on colour alone
- [x] Starting an Intent updates an open Backlog and the Intent's detail page in another tab live, without a reload
- [x] Covered by tests through the application, including a browser test for the live update
