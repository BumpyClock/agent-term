mod pool;
mod socket_proxy;
pub mod transport;
pub mod types;

pub use agentterm_shared::socket_path::socket_path_for;
pub use pool::{socket_alive, Pool, PoolConfig};
pub use types::ServerStatus;
