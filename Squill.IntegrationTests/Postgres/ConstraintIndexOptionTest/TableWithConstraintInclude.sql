CREATE TABLE booking
(
    booking_id integer,
    room       integer,
    guest      varchar(100),
    notes      text,
    CONSTRAINT pk_booking PRIMARY KEY (booking_id) INCLUDE (guest),
    CONSTRAINT uq_booking_room UNIQUE (room) INCLUDE (notes) WITH (fillfactor = 70)
);
