use serde::Deserialize;
use serde::Serialize;

#[derive(Deserialize, Debug)]
pub struct WaiterRequest {
  #[serde(rename = "Id")]
  pub id: String,
  #[serde(rename = "DispatcherUrl")]
  pub router_url: String,
  #[serde(rename = "NumberOfChannels")]
  pub number_of_channels: u8,
  #[serde(rename = "SentTime")]
  pub sent_time: String,
  #[serde(rename = "InitOnly", default)]
  pub init_only: bool,
}

#[derive(Serialize, Debug)]
pub struct WaiterResponse {
  #[serde(rename = "Id")]
  pub id: String,
}
