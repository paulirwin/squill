-- A rental transaction: a customer takes out an inventory item, handled by a staff member,
-- with an optional return_date that stays NULL until the item comes back. The unique index
-- prevents the same inventory item being rented to the same customer at the same instant.
CREATE TABLE rental
(
    rental_id    integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    rental_date  timestamp NOT NULL,
    inventory_id integer NOT NULL REFERENCES inventory (inventory_id),
    customer_id  integer NOT NULL REFERENCES customer (customer_id),
    return_date  timestamp,
    staff_id     integer NOT NULL REFERENCES staff (staff_id),
    last_update  timestamp NOT NULL DEFAULT now()
);

CREATE UNIQUE INDEX idx_unq_rental_rental_date_inventory_id_customer_id
    ON rental (rental_date, inventory_id, customer_id);

CREATE INDEX idx_fk_inventory_id ON rental (inventory_id);
-- Note: canonical Pagila carries idx_fk_customer_id and idx_fk_staff_id on the
-- payment table (not rental); PostgreSQL index names must be unique within a schema,
-- so they are declared there rather than duplicated here.
