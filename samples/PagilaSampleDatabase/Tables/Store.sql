-- A rental store. store and staff form a circular foreign-key pair: store.manager_staff_id
-- references staff, while staff.store_id references store. To keep the schema declarative we
-- express the store -> staff direction as an inline REFERENCES here; because staff is created
-- after store, the reference resolves once both tables exist. A unique index enforces that a
-- given staff member manages at most one store.
CREATE TABLE store
(
    store_id         integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    manager_staff_id integer NOT NULL REFERENCES staff (staff_id),
    address_id       integer NOT NULL REFERENCES address (address_id),
    last_update      timestamp NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX idx_unq_manager_staff_id ON store (manager_staff_id);
