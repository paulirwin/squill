-- Payments taken against rentals. Each payment ties a customer, the staff member who took it,
-- and the rental being paid for; amount is a fixed-precision numeric.
CREATE TABLE payment
(
    payment_id   integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    customer_id  integer NOT NULL REFERENCES customer (customer_id),
    staff_id     integer NOT NULL REFERENCES staff (staff_id),
    rental_id    integer NOT NULL REFERENCES rental (rental_id),
    amount       numeric(5,2) NOT NULL,
    payment_date timestamp NOT NULL
);

CREATE INDEX idx_fk_customer_id ON payment (customer_id);
CREATE INDEX idx_fk_staff_id ON payment (staff_id);
