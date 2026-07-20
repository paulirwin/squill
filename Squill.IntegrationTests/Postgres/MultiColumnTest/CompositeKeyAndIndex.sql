CREATE TABLE enrollment
(
    student_id integer PRIMARY KEY,
    course_id  integer NOT NULL,
    grade      integer NOT NULL
);

CREATE INDEX idx_enrollment_course_grade ON enrollment (course_id, grade);
