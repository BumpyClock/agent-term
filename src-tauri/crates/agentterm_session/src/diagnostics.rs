pub fn log(message: impl AsRef<str>) {
    eprintln!("{}", message.as_ref());
}

