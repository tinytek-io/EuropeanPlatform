# Staff blueprints

One authentik blueprint per environment (`<environment>.yaml`) lists the people who may sign in
as staff and their groups (`staff`, `admins`). The deploy mounts it next to the shared
`../blueprints/staff-identity.yaml`, which it depends on. Entries carry no passwords: staff sign in
with an emailed one-time code or a passkey they add themselves (stored in authentik's database).

Offboarding: set `is_active: false` on the entry (removing an entry from the file deletes
nothing in authentik). Never reuse a username for a different person.
