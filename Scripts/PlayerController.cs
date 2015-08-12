using UnityEngine;
using System.Collections;

public class PlayerController : MonoBehaviour {
	public float speed = 1;
	public float speed_modifier = 4;

	void Start() {
	}
	
	void Update() {
		var cam = Camera.main;
		var dir_right = cam.transform.right;
		var dir_up = Vector3.up;
		var dir_forward = Vector3.Cross(dir_right, dir_up);

		bool is_x_pos = (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow));
		bool is_x_neg = (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow));
		bool is_y_pos = (Input.GetKey(KeyCode.R) || Input.GetKey(KeyCode.PageUp));
		bool is_y_neg = (Input.GetKey(KeyCode.F) || Input.GetKey(KeyCode.PageDown));
		bool is_z_pos = (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow));
		bool is_z_neg = (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow));

		int speed_x = (is_x_pos?1:0) - (is_x_neg?1:0);
		int speed_y = (is_y_pos?1:0) - (is_y_neg?1:0);
		int speed_z = (is_z_pos?1:0) - (is_z_neg?1:0);
		Vector3 speed_v = (speed_x * dir_right) + (speed_y * dir_up) + (speed_z * dir_forward);

		if (speed_v.magnitude > 0) {
			if (speed_modifier > 1e-5f) {
				if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) speed_v /= speed_modifier;
				if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) speed_v *= speed_modifier;
			}

			transform.position += speed_v * speed;
			transform.rotation = Quaternion.LookRotation(dir_forward);
		}

		if (Input.GetKey(KeyCode.Escape)) Application.Quit();
	}
}
